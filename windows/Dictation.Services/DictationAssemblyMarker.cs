namespace Dictation.Services;

/// <summary>
/// Anchor type for reflecting over this assembly — the project-structure tests
/// use it to assert that the dictation core stays plain net9.0 with no target
/// platform.
/// </summary>
/// <remarks>
/// <para>
/// Intentionally empty. Nothing in the app uses it, and it stays for that test
/// when the dormant code the app never reached is removed. This project holds
/// the <c>DllImport</c> declarations in <c>Win32FocusedApplicationResolver</c>,
/// which is precisely why the guarantee is worth a named handle: a declaration
/// compiles under any target framework, so nothing about that file complains
/// when somebody "fixes" the platform warning by moving this project to
/// <c>net9.0-windows</c>. The structure test is the only thing that would
/// notice, and it needs somewhere stable to point.
/// </para>
/// <para>
/// A test could reflect through <see cref="DictationSession"/> instead, but
/// that couples the platform assertion to a type that exists for an unrelated
/// reason: move or rename the session and the guarantee silently loses its
/// handle. A marker cannot be deleted by accident, and unlike an
/// assembly-name string literal it cannot be left stale by a rename.
/// </para>
/// </remarks>
public static class DictationAssemblyMarker
{
}
