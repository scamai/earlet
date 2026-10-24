using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.App;

/// <summary>
/// <see cref="ISecureFieldProbe"/> against the real desktop, through UI
/// Automation's <c>IsPassword</c> property.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why UIA and not a Win32 style bit.</b> The classic answer is
/// <c>GetWindowLong</c> plus <c>ES_PASSWORD</c>, which works only for a native
/// <c>EDIT</c> control. Almost nothing a person signs into today is one:
/// Chrome, Edge, Electron applications, WinUI and WPF all draw their own text
/// boxes and expose them through UI Automation and nowhere else.
/// <c>AutomationElement.FocusedElement</c> answers for all of them, and
/// <c>IsPasswordProperty</c> is the property every framework sets on a masked
/// field because screen readers depend on it.
/// </para>
/// <para>
/// <b>Bounded, because UIA is a cross-process call.</b> It marshals into
/// whatever application has focus, and a busy or hung target can take an
/// unbounded time to answer — on the dictation path, that would be a freeze
/// between the user letting go of the key and anything happening, which is the
/// worst place in the product to add a stall. The query therefore runs on a
/// thread-pool thread with a hard deadline and the caller stops waiting when it
/// expires. A late answer is discarded, never applied to a dictation that has
/// already moved on.
/// </para>
/// <para>
/// <b>Every failure is Unknown, and Unknown means carry on.</b> No focused
/// element, a property the target does not publish, a COM exception, a timeout,
/// a machine where UIA is restricted — all of them answer the same way, and the
/// session treats that as an ordinary field. A guard that failed closed would
/// switch dictation off silently on machines that have no password box in
/// sight, which is the failure this whole feature exists to prevent.
/// </para>
/// </remarks>
public sealed class UiaSecureFieldProbe : ISecureFieldProbe
{
    /// <summary>
    /// How long the focused-element query gets before the answer is abandoned.
    /// </summary>
    /// <remarks>
    /// A normal answer is single-digit milliseconds; this is sized for the
    /// pathological case, not the usual one. It sits on the path between the
    /// user releasing the key and the recogniser starting — a stage that
    /// already costs 1.3–3.0 seconds on a warm hold — so 250 ms is inside the
    /// noise of what is already there and is still short enough that a hung
    /// target cannot make dictation feel stuck.
    /// </remarks>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(250);

    private readonly ILogger<UiaSecureFieldProbe> _logger;
    private readonly TimeSpan _timeout;

    public UiaSecureFieldProbe(ILogger<UiaSecureFieldProbe> logger, TimeSpan? timeout = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout ?? DefaultTimeout;
    }

    public SecureFieldState Probe()
    {
        // Run on the pool rather than on the calling thread. The caller is the
        // dispatcher, and a UIA call that blocks there blocks the window the
        // words are going into as well as the pill that is meant to be
        // animating.
        var query = Task.Run(ReadFocusedElement);

        if (!query.Wait(_timeout))
        {
            // The task keeps running and its result is discarded. There is no
            // way to cancel a UIA call in flight, and abandoning the WAIT is
            // the whole point: a hung target must cost this dictation nothing
            // beyond the deadline.
            _logger.LogDebug(
                "The password-field probe did not answer within {Timeout}ms; treating the field as ordinary",
                _timeout.TotalMilliseconds);
            return SecureFieldState.Unknown;
        }

        return query.Result;
    }

    private SecureFieldState ReadFocusedElement()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                // No focused element at all — the desktop, a lock screen, a
                // shell transition. Not a password box, and not an error.
                return SecureFieldState.Unknown;
            }

            // GetCurrentPropertyValue with the not-supported sentinel rather
            // than .Current.IsPassword: the property accessor throws for an
            // element whose provider does not publish it, and "this control
            // does not answer that question" is an ordinary answer here rather
            // than an exceptional one.
            var value = focused.GetCurrentPropertyValue(
                AutomationElement.IsPasswordProperty,
                ignoreDefaultValue: true);

            if (value == AutomationElement.NotSupported || value is not bool isPassword)
            {
                return SecureFieldState.Unknown;
            }

            return isPassword ? SecureFieldState.Secure : SecureFieldState.NotSecure;
        }
        catch (ElementNotAvailableException)
        {
            // The element went away between being focused and being read — a
            // dialog closing under the user's hand. Ordinary.
            return SecureFieldState.Unknown;
        }
        catch (Exception ex)
        {
            // COM failures, restricted UIA, a provider that faults. All of them
            // are the same fact from the session's side: no answer.
            _logger.LogDebug(ex, "The password-field probe could not read the focused element");
            return SecureFieldState.Unknown;
        }
    }
}
