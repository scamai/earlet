using System;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Dictation.Core;
using Dictation.Services;
using Dictation.Services.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.App;

/// <summary>
/// <see cref="ISpanObserverTarget"/> against the real desktop through UI
/// Automation (the span-observer contract §1, §3). Every call runs on the thread pool
/// under the <c>t_uia</c> deadline, the same shape as
/// <see cref="UiaSecureFieldProbe"/>, but in the opposite direction: every
/// failure is a failure value, which the observer turns into
/// <c>unobservable</c> (invariant O2). Nothing here stores field text; the
/// one whole-field read goes back to the observer, which drops it.
/// </summary>
internal sealed class UiaSpanObserverTarget : ISpanObserverTarget
{
    private readonly ILogger _logger;
    private readonly TimeSpan _tUia;
    private readonly SpanObserverKeyboardSignal _keyboard;
    private AutomationElement? _focused;
    /// <summary>The element whose TextPattern offsets refer to: the focused element, then the §3.2 B descendant.</summary>
    private AutomationElement? _element;
    private TextPattern? _text;

    private UiaSpanObserverTarget(ILogger logger, TimeSpan tUia, SpanObserverKeyboardSignal keyboard)
    {
        _logger = logger;
        _tUia = tUia;
        _keyboard = keyboard;
    }

    public SpanObserverTargetIdentity Identity { get; private set; } = SpanObserverTargetIdentity.Unresolved;

    public SpanObserverKeyboardInput? LastKeyboardInput => _keyboard.Last;

    /// <summary>Resolves the focused element once at <c>t0</c>. Never throws; an unresolvable focus yields <see cref="SpanObserverTargetIdentity.Unresolved"/>.</summary>
    public static UiaSpanObserverTarget Resolve(ILogger logger, SpanObserverKeyboardSignal keyboard, SpanObserverOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(options);
        var t = new UiaSpanObserverTarget(logger, TimeSpan.FromMilliseconds(options.TUiaMs), keyboard);
        var r = SpanObserver.Bounded(() =>
        {
            var focused = AutomationElement.FocusedElement ?? throw new SpanObserverCallException(SpanObserverFailures.NoFocus);
            var pid = focused.Current.ProcessId;
            string name;
            try
            {
                name = Process.GetProcessById(pid).ProcessName;
            }
            catch (Exception)
            {
                name = string.Empty;
            }
            var identity = new SpanObserverTargetIdentity(name, pid, focused.Current.ClassName ?? string.Empty, focused.Current.NativeWindowHandle, false);
            return (focused, identity);
        }, t._tUia);
        if (r.Ok)
        {
            t._focused = r.Value.focused;
            t._element = r.Value.focused;
            t.Identity = r.Value.identity;
        }
        else
        {
            logger.LogDebug("span observer: focused element unavailable ({Reason})", r.Failure);
        }
        return t;
    }

    public SpanObserverResult<SecureFieldState> ProbeSecureField() => Bounded(() =>
    {
        // Same read as UiaSecureFieldProbe; the observer maps Unknown to "stop".
        var value = _focused!.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
        if (value == AutomationElement.NotSupported || value is not bool isPassword)
        {
            return SecureFieldState.Unknown;
        }
        return isPassword ? SecureFieldState.Secure : SecureFieldState.NotSecure;
    });

    public SpanObserverResult<SpanObserverSelection> GetSelection() => Bounded(() =>
    {
        var tp = Pattern();
        var sel = tp.GetSelection();
        if (sel is null || sel.Length != 1)
        {
            throw new SpanObserverCallException(SpanObserverFailures.NoSelection);
        }
        // The caret offset without a text transfer: walk the start back and count the units moved.
        var toStart = sel[0].Clone();
        var moved = toStart.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1_000_000);
        var length = (sel[0].GetText(256) ?? string.Empty).Length;
        return new SpanObserverSelection(-moved, length);
    });

    public SpanObserverResult<string> ReadRange(int start, int length) => Bounded(() =>
    {
        var tp = Pattern();
        var r = tp.DocumentRange.Clone();
        r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
        r.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, Math.Max(0, start) + Math.Max(0, length));
        r.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, Math.Max(0, start));
        return r.GetText(Math.Max(0, length)) ?? string.Empty;
    });

    /// <summary>§3.2 B: exactly one descendant search per hold; the text returned here is dropped by the observer.</summary>
    public SpanObserverResult<string> ReadText(int maxChars) => Bounded(() =>
    {
        var focused = _focused ?? throw new SpanObserverCallException(SpanObserverFailures.NoFocus);
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true),
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        var element = focused.TryGetCurrentPattern(TextPattern.Pattern, out _)
            ? focused
            : focused.FindFirst(TreeScope.Descendants, condition);
        if (element is null || !element.TryGetCurrentPattern(TextPattern.Pattern, out var p) || p is not TextPattern tp)
        {
            throw new SpanObserverCallException(SpanObserverFailures.NoTextPattern);
        }
        _element = element;
        _text = tp;
        return tp.DocumentRange.GetText(maxChars) ?? string.Empty;
    });

    public IDisposable? Subscribe(Action<SpanObserverSignal> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var element = _element;
        if (element is null)
        {
            return null;
        }
        var identity = Identity;
        AutomationEventHandler onText = (_, _) => handler(SpanObserverSignal.TextChanged);
        AutomationFocusChangedEventHandler onFocus = (sender, _) =>
        {
            try
            {
                if (sender is AutomationElement e
                    && (e.Current.ProcessId != identity.ProcessId || e.Current.NativeWindowHandle != identity.FocusHandle))
                {
                    handler(SpanObserverSignal.FocusLost);
                }
            }
            catch (Exception)
            {
                handler(SpanObserverSignal.FocusLost);
            }
        };

        // Not SpanObserver.Bounded: a registration that finishes after t_uia
        // would stay registered for the life of the process, and a focus
        // handler that fails after the text handler went in would leave that
        // one. BoundedRegistration undoes both.
        var r = BoundedRegistration.Register(
            [
                () =>
                {
                    Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, element, TreeScope.Element, onText);
                    return BoundedRegistration.UndoWith(
                        () => Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, element, onText));
                },
                () =>
                {
                    Automation.AddAutomationFocusChangedEventHandler(onFocus);
                    return BoundedRegistration.UndoWith(() => Automation.RemoveAutomationFocusChangedEventHandler(onFocus));
                },
            ],
            _tUia);
        if (!r.Ok)
        {
            _logger.LogDebug("span observer: no change subscription ({Reason}); polling", r.Failure);
            return null;
        }
        return r.Value;
    }

    private TextPattern Pattern()
    {
        if (_text is not null)
        {
            return _text;
        }
        var element = _element ?? throw new SpanObserverCallException(SpanObserverFailures.NoFocus);
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var p) || p is not TextPattern tp)
        {
            throw new SpanObserverCallException(SpanObserverFailures.NoTextPattern);
        }
        _text = tp;
        return tp;
    }

    private SpanObserverResult<T> Bounded<T>(Func<T> call)
    {
        if (_focused is null)
        {
            return SpanObserverResult<T>.Failed(SpanObserverFailures.NoFocus);
        }
        var r = SpanObserver.Bounded(call, _tUia);
        if (!r.Ok)
        {
            _logger.LogDebug("span observer: UIA call failed ({Reason}) after {Ms}ms", r.Failure, (int)r.ElapsedMs);
        }
        return r;
    }
}
