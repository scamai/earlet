using System.Reflection;
using System.Runtime.Versioning;
using Dictation.Services;

namespace Dictation.Services.Tests;

/// <summary>
/// Guards the boundary dictation is built on: policy is cross-platform,
/// adapters are platform-bound.
///
/// <para>
/// <c>Dictation.Services</c> holds real <c>DllImport</c> declarations —
/// <c>Win32FocusedApplicationResolver</c> calls <c>GetForegroundWindow</c> and
/// friends — and a declaration compiles happily under <c>net9.0</c>. It is the
/// <em>call</em> that needs Windows, and every call sits behind
/// <see cref="IFocusedApplicationResolver"/> so no test reaches one. That is a
/// deliberate trade recorded in the csproj, and it is exactly the kind of trade
/// somebody undoes in good faith while chasing a platform analyser warning.
/// This file is what turns that undo into a red build instead of a silent loss.
/// </para>
///
/// <para>
/// What is lost, concretely: <c>Dictation.Services.Tests</c> is executable
/// evidence the dictation state machine works on a checkout with no Windows
/// machine in it. A <c>net9.0-windows</c> assembly on either side of that pair
/// would strand every dictation assertion behind a Windows machine.
/// </para>
/// </summary>
public class DictationProjectStructureTests
{
    [Fact]
    public void Dictation_core_targets_net9_with_no_platform()
    {
        var assembly = typeof(DictationAssemblyMarker).Assembly;

        AssertPlatformNeutralNet9(assembly);
    }

    [Fact]
    public void Dictation_tests_target_net9_with_no_platform()
    {
        // The test project is half the invariant: a net9.0-windows test
        // assembly would silently drop off the portable lane.
        var assembly = typeof(DictationProjectStructureTests).Assembly;

        AssertPlatformNeutralNet9(assembly);
    }

    [Fact]
    public void The_marker_lives_in_the_assembly_it_speaks_for()
    {
        // Non-vacuity for the two assertions above. The marker is a type with
        // no members, so moving it into another project — the test project,
        // say, while tidying — costs nothing at compile time and would leave
        // Dictation_core_targets_net9_with_no_platform asserting about
        // whichever assembly it landed in rather than about the dictation
        // core.
        Assert.Same(
            typeof(DictationSession).Assembly,
            typeof(DictationAssemblyMarker).Assembly);
    }

    [Fact]
    public void Dictation_tests_reference_no_windows_assembly()
    {
        // Asserted rather than eyeballed: the compiler records what this
        // assembly actually binds against, so a Windows type arriving through
        // a helper nobody re-read still trips it. In particular this is the
        // only automated check that Dictation.Infrastructure — which is
        // net9.0-windows and holds the reading clipboard — has not been
        // referenced from either of these two projects for convenience.
        AssertNoWindowsReference(typeof(DictationProjectStructureTests).Assembly);
        AssertNoWindowsReference(typeof(DictationAssemblyMarker).Assembly);
    }

    private static void AssertNoWindowsReference(Assembly assembly)
    {
        var references = assembly.GetReferencedAssemblies();

        // Non-vacuity: the compiler drops references nothing binds against, so
        // an empty list would make every assertion below pass for free.
        //
        // Dictation.Speech, because both assemblies are sure to bind it:
        // Dictation.Services's single ProjectReference, reached by the core
        // through IClipboard and IPasteKeystroke and by the tests through the
        // seams DictationSessionTests fakes.
        Assert.Contains(references, r => r.Name == "Dictation.Speech");

        foreach (var reference in references)
        {
            var name = reference.Name ?? string.Empty;

            // One carve-out. Microsoft.Win32.Primitives is a shared-framework
            // assembly: it ships inside Microsoft.NETCore.App on Linux and macOS
            // exactly as it does on Windows. It is bound here only because
            // Win32FocusedApplicationResolver catches Win32Exception, which lives
            // in it, so the name is a historical artifact and the substring rule
            // below, which cannot tell the two apart, would report it as a
            // platform binding and fail a project that is perfectly portable.
            //
            // Named exactly rather than allowed by prefix, so the rule stays
            // tight: a future Microsoft.Win32.Registry reference still fails, and
            // it should. That assembly also ships cross-platform, but a portable
            // policy layer reaching for the registry is a design problem whatever
            // the loader thinks of it.
            if (name == "Microsoft.Win32.Primitives")
            {
                continue;
            }

            Assert.False(
                IsPlatformBound(name),
                $"{assembly.GetName().Name} references {name}, which is platform-bound.");
        }
    }

    /// <summary>
    /// The net9.0-windows projects in this tree, by assembly name: the app
    /// (its assembly is <c>Dictation</c>) and its two Win32 adapter projects.
    /// </summary>
    private static readonly HashSet<string> PlatformBoundProjects = new(StringComparer.Ordinal)
    {
        "Dictation", "Dictation.App", "Dictation.Infrastructure", "Dictation.Platform",
    };

    /// <summary>
    /// WPF (Presentation*, WindowsBase), WinForms and the Win32 shims, plus
    /// this product's own platform-bound projects: every net9.0-windows
    /// assembly a dictation assembly could bind is one of these.
    /// </summary>
    private static bool IsPlatformBound(string name) =>
        name.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Win32", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase) ||
        PlatformBoundProjects.Contains(name);

    [Theory]
    [InlineData("Dictation")]
    [InlineData("Dictation.App")]
    [InlineData("Dictation.Infrastructure")]
    [InlineData("Dictation.Platform")]
    [InlineData("PresentationFramework")]
    [InlineData("WindowsBase")]
    [InlineData("System.Windows.Forms")]
    [InlineData("Microsoft.Win32.Registry")]
    public void Each_platform_bound_assembly_is_recognised(string name)
    {
        // The reference check above can only fail when a real reference
        // arrives, so the names it refuses are pinned here: dropping one from
        // the rule turns this red.
        Assert.True(IsPlatformBound(name), name);
    }

    [Theory]
    [InlineData("Dictation.Core")]
    [InlineData("Dictation.Services")]
    [InlineData("Dictation.Speech")]
    [InlineData("Microsoft.Extensions.Logging.Abstractions")]
    public void Portable_assemblies_are_not_refused(string name)
    {
        Assert.False(IsPlatformBound(name), name);
    }

    private static void AssertPlatformNeutralNet9(Assembly assembly)
    {
        var targetFramework = assembly.GetCustomAttribute<TargetFrameworkAttribute>();
        Assert.NotNull(targetFramework);
        Assert.Equal(".NETCoreApp,Version=v9.0", targetFramework!.FrameworkName);

        // net9.0-windows is what emits this; plain net9.0 does not.
        var targetPlatform = assembly.GetCustomAttribute<TargetPlatformAttribute>();
        Assert.Null(targetPlatform);
    }
}
