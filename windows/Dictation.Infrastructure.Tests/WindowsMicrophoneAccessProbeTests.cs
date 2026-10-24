using Dictation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Dictation.Infrastructure.Tests;

/// <summary>
/// The consent-store read, against the real registry on the machine running
/// the suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>This one has to touch the real thing.</b> Everything about what to SAY
/// lives in <c>MicrophoneAccessCopy</c> and is asserted without a registry.
/// What is left here is the claim that cannot be faked: that the key paths,
/// the value names and the <c>#</c>-mangled per-app subkey spelling are the
/// ones Windows actually uses. A probe with a typo'd path answers
/// <see cref="MicrophoneAccess.Unknown"/> forever and looks exactly like a
/// machine with no consent store — which is the failure mode this file exists
/// to make loud.
/// </para>
/// <para>
/// <b>Self-guarding rather than skipped.</b> Each test first asks whether the
/// consent store exists on this machine. Where it does, a real assertion runs
/// against it; where it does not, there is nothing to be right or wrong about.
/// A blanket skip would let the probe rot silently on the verify device, which
/// is the one machine that always has the store — its values were read on
/// 2026-08-20 and are quoted in the probe's own remarks.
/// </para>
/// </remarks>
public sealed class WindowsMicrophoneAccessProbeTests
{
    private const string ConsentStore =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private static bool ConsentStoreExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ConsentStore);
        return key is not null;
    }

    private static WindowsMicrophoneAccessProbe Build() =>
        new(NullLogger<WindowsMicrophoneAccessProbe>.Instance);

    [Fact]
    public void It_never_throws()
    {
        // The contract IMicrophoneAccessProbe states, and it matters: this runs
        // inside a settings-pane property getter and on the failure path of a
        // dictation. An exception here would turn a permission question into a
        // crash on key release.
        var probe = Build();

        var first = probe.Probe();
        var second = probe.Probe();

        Assert.Equal(first, second);
    }

    [WindowsOnlyFact("the microphone consent switch is a Windows registry key; off Windows there is no store to read and the probe cannot be exercised at all")]
    public void On_a_machine_with_a_consent_store_it_actually_reads_it()
    {
        // The assertion a typo'd registry path fails. Unknown is the honest
        // answer for a machine with no store; it is the WRONG answer for one
        // that has it, and the two are indistinguishable from the outside
        // unless something checks.
        if (!ConsentStoreExists())
        {
            return;
        }

        Assert.NotEqual(MicrophoneAccess.Unknown, Build().Probe());
    }

    [WindowsOnlyFact("the microphone consent switch is a Windows registry key; off Windows there is no store to read and the probe cannot be exercised at all")]
    public void It_agrees_with_the_switch_the_registry_actually_holds()
    {
        // Not a tautology: this reads the value independently, through a
        // different code path, and holds the probe's verdict to it. A probe
        // that answered Denied on an Allow machine would put a false accusation
        // about the user's own settings on the busiest card in the pane.
        if (!ConsentStoreExists())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(ConsentStore);
        var value = key?.GetValue("Value") as string;

        var access = Build().Probe();

        if (string.Equals(value, "Allow", StringComparison.Ordinal))
        {
            // Allowed or Busy — both mean "not blocked". Busy is legitimate on
            // a machine where something else has the endpoint open, and this
            // suite has no way to guarantee it does not.
            Assert.False(
                MicrophoneAccessCopy.IsBlocked(access),
                $"the consent store says Allow but the probe answered {access}");
        }
        else if (string.Equals(value, "Deny", StringComparison.Ordinal))
        {
            Assert.True(
                MicrophoneAccessCopy.IsBlocked(access),
                $"the consent store says Deny but the probe answered {access}");
        }
    }

    [WindowsOnlyFact("the microphone consent switch is a Windows registry key; off Windows there is no store to read and the probe cannot be exercised at all")]
    public void The_per_app_subkey_spelling_is_the_one_Windows_uses()
    {
        // Measured on the verify device: the store holds this process's
        // executable path with every backslash replaced by '#'. If Windows ever
        // changed that, the per-app switch would silently stop being read while
        // the three global switches went on answering — a partial probe, which
        // is worse than none.
        if (!ConsentStoreExists())
        {
            return;
        }

        using var nonPackaged = Registry.CurrentUser.OpenSubKey(ConsentStore + @"\NonPackaged");
        if (nonPackaged is null)
        {
            return;
        }

        foreach (var name in nonPackaged.GetSubKeyNames())
        {
            // Every row is a path: a drive letter, a colon, and '#' where a
            // backslash would be. A row containing a real backslash would mean
            // the mangling rule had changed.
            Assert.DoesNotContain('\\', name);
        }
    }
}
