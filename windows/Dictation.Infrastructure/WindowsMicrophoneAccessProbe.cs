using System.Diagnostics;
using Dictation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Dictation.Infrastructure;

/// <summary>
/// Whether Windows will let this process open a microphone, read out of the
/// Capability Access Manager's consent store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a registry read and not an API.</b> There is a supported API for this
/// — <c>AppCapability.CheckAccess</c> — and it is a WinRT type whose identity
/// resolution for an unpackaged desktop process is not something to take on
/// inside a settings-pane getter. The consent store is where the Settings app
/// itself writes those switches, its layout is stable across Windows 10 and 11,
/// and every value below was READ OFF THE VERIFY DEVICE on 2026-08-20 rather
/// than taken from documentation:
/// </para>
/// <code>
/// HKCU\...\ConsentStore\microphone              Value = Allow   ← "Microphone access" for this user
/// HKLM\...\ConsentStore\microphone              Value = Allow   ← the machine-wide master
/// HKCU\...\ConsentStore\microphone\NonPackaged  Value = Allow   ← "Let desktop apps access your microphone"
/// HKCU\...\NonPackaged\C:#…#&lt;app&gt;.exe          (no Value)      ← no per-app decision; inherits
///                                               LastUsedTimeStart / LastUsedTimeStop
/// HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy           ← absent; no policy on this box
/// </code>
/// <para>
/// The per-app subkey name is the executable's full path with every backslash
/// replaced by <c>#</c>. That is measured too: the device's store held the
/// measured build under <c>C:#Users#someone#…#&lt;app&gt;.exe</c>.
/// </para>
/// <para>
/// <b>It is a heuristic and it is treated as one.</b> Anything unreadable,
/// absent or unrecognised answers <see cref="MicrophoneAccess.Unknown"/>, which
/// every surface renders as "carry on" — the copy layer's own rule. A probe
/// that guessed <c>Denied</c> would put a false accusation about the user's own
/// settings on the busiest card in the pane.
/// </para>
/// <para>
/// <b>Never throws.</b> <see cref="IMicrophoneAccessProbe"/> requires it, and
/// this runs inside a property getter that a settings pane binds.
/// </para>
/// </remarks>
public sealed class WindowsMicrophoneAccessProbe : IMicrophoneAccessProbe
{
    private const string ConsentStore =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private const string NonPackaged = ConsentStore + @"\NonPackaged";

    private const string AppPrivacyPolicy =
        @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";

    /// <summary>
    /// The policy value's "force deny" setting. 0 is "the user decides", 1 is
    /// force-allow, 2 is force-deny — and 2 is the one that greys the Settings
    /// toggle out, which is why it gets its own state.
    /// </summary>
    private const int PolicyForceDeny = 2;

    private readonly ILogger<WindowsMicrophoneAccessProbe> _logger;

    /// <summary>
    /// This executable's path, mangled the way the consent store spells it.
    /// Computed once: it cannot change while the process lives, and the getter
    /// this feeds is bound by a settings pane.
    /// </summary>
    private readonly string? _selfKey;

    public WindowsMicrophoneAccessProbe(ILogger<WindowsMicrophoneAccessProbe> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _selfKey = TryResolveSelfKey();
    }

    /// <inheritdoc />
    public MicrophoneAccess Probe()
    {
        try
        {
            return Read();
        }
        catch (Exception ex)
        {
            // Unknown, never Denied. See the class remarks: the cost of being
            // wrong in that direction is telling a user their own settings are
            // at fault when they are not.
            _logger.LogDebug(ex, "The microphone consent store could not be read; access is unknown");
            return MicrophoneAccess.Unknown;
        }
    }

    private MicrophoneAccess Read()
    {
        // ── 1. Policy first ───────────────────────────────────────────────
        //
        // It overrides everything below it, and it is the one denial the user
        // cannot fix — so it must not be reported as one they can.
        if (ReadDword(Registry.LocalMachine, AppPrivacyPolicy, "LetAppsAccessMicrophone")
            == PolicyForceDeny)
        {
            return MicrophoneAccess.DeniedByPolicy;
        }

        // ── 2. The three switches, coarsest first ─────────────────────────
        //
        // Machine master, then the user's "Microphone access", then "Let
        // desktop apps access your microphone" — which is the one that
        // actually governs an unpackaged app like this one, and the one a
        // setup wizard or a hardened image is likeliest to have turned off.
        var anyRead = false;

        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, ConsentStore),
            (Registry.CurrentUser, ConsentStore),
            (Registry.CurrentUser, NonPackaged),
        })
        {
            switch (ReadConsent(hive, path))
            {
                case "Deny":
                    return MicrophoneAccess.Denied;
                case "Allow":
                    anyRead = true;
                    break;
            }
        }

        // ── 3. This application's own row ─────────────────────────────────
        //
        // Absent on the verify device, which is the ordinary case: no per-app
        // decision has been made, so the switches above govern. A "Deny" here
        // is the per-app switch under Privacy & security → Microphone → the
        // desktop-apps list.
        if (_selfKey is not null)
        {
            switch (ReadConsent(Registry.CurrentUser, NonPackaged + "\\" + _selfKey))
            {
                case "Deny":
                    return MicrophoneAccess.Denied;
                case "Allow":
                    anyRead = true;
                    break;
            }
        }

        if (!anyRead)
        {
            // Nothing readable said anything. Not a denial and not a clearance.
            return MicrophoneAccess.Unknown;
        }

        // ── 4. Busy ───────────────────────────────────────────────────────
        return AnotherApplicationIsCapturing() ? MicrophoneAccess.Busy : MicrophoneAccess.Allowed;
    }

    /// <summary>
    /// Whether some OTHER unpackaged application currently holds a capture
    /// session, by the consent store's own bookkeeping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each row under <c>NonPackaged</c> carries <c>LastUsedTimeStart</c> and
    /// <c>LastUsedTimeStop</c>. Windows stamps the start when an application
    /// opens the endpoint and the stop when it closes it, so a row whose start
    /// is later than its stop is an application that has it open right now.
    /// This is the mechanism the Settings page's own "currently in use" marker
    /// reads, and the same rows the device probe printed above.
    /// </para>
    /// <para>
    /// <b>It is a heuristic, and the copy it feeds says nothing it cannot
    /// support.</b> The row names an executable path, but a stale row from an
    /// application that crashed without a stop stamp reads identically to a
    /// live one — so the sentence is "something else is using the microphone"
    /// and never names the application. macOS names an owner because it asks
    /// the audio system; this asks a registry.
    /// </para>
    /// <para>
    /// Our own row is skipped. Dictation holds the endpoint for the length of
    /// every hold, so counting it would report the app as busy with itself for the
    /// exact seconds the user is dictating.
    /// </para>
    /// </remarks>
    private bool AnotherApplicationIsCapturing()
    {
        using var root = Registry.CurrentUser.OpenSubKey(NonPackaged);
        if (root is null)
        {
            return false;
        }

        foreach (var name in root.GetSubKeyNames())
        {
            if (_selfKey is not null
                && string.Equals(name, _selfKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var row = root.OpenSubKey(name);
            if (row is null)
            {
                continue;
            }

            // QWORDs in FILETIME ticks. Both must be present: a row with a
            // start and no stop at all has never been closed by this mechanism
            // and is not evidence of anything.
            if (row.GetValue("LastUsedTimeStart") is long start
                && row.GetValue("LastUsedTimeStop") is long stop
                && start > stop)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadConsent(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }

    private static int? ReadDword(RegistryKey hive, string path, string name)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue(name) as int?;
    }

    /// <summary>
    /// This process's executable path with backslashes replaced by <c>#</c> —
    /// the consent store's own spelling, measured on the device.
    /// </summary>
    private string? TryResolveSelfKey()
    {
        try
        {
            var path = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;

            return string.IsNullOrEmpty(path) ? null : path.Replace('\\', '#');
        }
        catch (Exception ex)
        {
            // A process that cannot name its own executable simply has no
            // per-app row to look up; the three switches above still answer.
            _logger.LogDebug(ex, "Could not resolve this process's path for the consent store lookup");
            return null;
        }
    }
}
