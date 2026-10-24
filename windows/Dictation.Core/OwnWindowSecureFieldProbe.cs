using Dictation.Services;

namespace Dictation.Core;

/// <summary>
/// The password-field probe, except while one of Dictation's own windows is
/// in front: none of them has a password field, so the answer is
/// <see cref="SecureFieldState.NotSecure"/> without asking UI Automation.
/// </summary>
/// <remarks>
/// <para>
/// "Try dictation" makes a hold into Dictation's own window the first thing a
/// new user does, and the release-time probe runs on the UI thread: the
/// session raises the end of the capture there and asks before its first
/// await. The UIA probe then waits up to 250 ms for a pool thread to read the
/// focused element. When that element is in this process, WPF's UIA provider
/// answers through the UI thread that is waiting, so the answer most likely
/// comes only at the deadline, as Unknown, with the window, the indicator and
/// the keyboard hook stalled meanwhile. Reasoned from the code, never seen on
/// Windows. Asking nothing is right whatever the timing: the window's fields
/// (the practice field, the teach box) are ordinary text.
/// </para>
/// <para>
/// Never throws, like every <see cref="ISecureFieldProbe"/>: a front-window
/// check that fails asks UI Automation as before.
/// </para>
/// </remarks>
/// <param name="outside">The probe for every other application.</param>
/// <param name="ownWindowInFront">Whether the foreground window belongs to
/// this process.</param>
public sealed class OwnWindowSecureFieldProbe(ISecureFieldProbe outside, Func<bool> ownWindowInFront) : ISecureFieldProbe
{
    public SecureFieldState Probe()
    {
        bool own;
        try
        {
            own = ownWindowInFront();
        }
        catch (Exception)
        {
            own = false;
        }

        return own ? SecureFieldState.NotSecure : outside.Probe();
    }
}
