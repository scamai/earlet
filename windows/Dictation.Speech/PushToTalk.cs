using System;
using Microsoft.Extensions.Logging;

namespace Dictation.Speech;

/// <summary>
/// A global hotkey, as the three things <c>RegisterHotKey</c> takes plus the
/// one thing a person needs to be told.
/// </summary>
/// <param name="Modifiers">
/// The <c>MOD_*</c> bit field, including <c>MOD_NOREPEAT</c>.
/// </param>
/// <param name="VirtualKey">
/// The <c>VK_*</c> code of the non-modifier key. This is also the key whose
/// physical state is polled to find the release, so it must be a real key and
/// not a modifier.
/// </param>
/// <param name="Display">
/// How the combination is written for a human — the HUD (P-08) and the
/// keyboard-fallback prompt (D-01) both show it.
/// </param>
public sealed record HotkeyBinding(uint Modifiers, uint VirtualKey, string Display)
{
    /// <summary>
    /// The verbs <see cref="Display"/> may open with, longest first.
    /// </summary>
    private static readonly string[] Verbs = ["Double-tap ", "Hold "];

    /// <summary>
    /// The key alone, with the verb <see cref="Display"/> carries stripped off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Display"/> is written to stand alone ("Hold Right Alt"), which
    /// is right in a sentence and wrong on a keycap — a key drawn as a key
    /// should not have the word "Hold" printed on it.
    /// </para>
    /// <para>
    /// This also gives a name to a slice that was already being taken by hand:
    /// <c>DictationHotkeys</c> built its double-tap labels as
    /// <c>"Double-tap " + c.Display[5..]</c>, a literal 5 that silently means
    /// "the length of the word Hold and a space" and would take the wrong
    /// characters the moment any hold label opened with a different verb.
    /// </para>
    /// <para>
    /// A display with no verb is returned unchanged, so this is safe on a
    /// binding built from anywhere.
    /// </para>
    /// </remarks>
    public string KeyName
    {
        get
        {
            foreach (var verb in Verbs)
            {
                if (Display.StartsWith(verb, StringComparison.Ordinal))
                {
                    return Display[verb.Length..];
                }
            }

            return Display;
        }
    }
}

/// <summary>
/// The bindings this feature claims, and the Win32 numbers behind them.
/// </summary>
/// <remarks>
/// The constants are duplicated from <c>winuser.h</c> rather than referenced,
/// because they live in the cross-platform half on purpose: keeping them here
/// is what lets the binding be asserted on a Linux checkout, and they are
/// frozen numbers in a thirty-year-old header, not a dependency that can move.
/// </remarks>
public static class WorkflowHotkeys
{
    /// <summary><c>MOD_ALT</c>.</summary>
    public const uint ModAlt = 0x0001;

    /// <summary><c>MOD_CONTROL</c>.</summary>
    public const uint ModControl = 0x0002;

    /// <summary><c>MOD_SHIFT</c>.</summary>
    public const uint ModShift = 0x0004;

    /// <summary>
    /// <c>MOD_NOREPEAT</c> — one <c>WM_HOTKEY</c> per press instead of a stream
    /// of them while the key is held. Exactly what push-to-talk wants, and
    /// available since Windows 7.
    /// </summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary><c>VK_SPACE</c>.</summary>
    public const uint VkSpace = 0x20;

    /// <summary>
    /// Hold this to talk: <b>Ctrl + Alt + Space</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing registers this any more</b>, and nothing calls
    /// <see cref="Fallback"/> either: this class compiles in with no caller.
    /// It is not a key anybody should be told to press.
    /// </para>
    /// <para>
    /// Chosen for the demo, not for the ages — a hotkey is a user-facing
    /// choice and this one is the loop's, recorded here so it is one edit to
    /// change. The reasoning: it is comfortable to hold with the left hand
    /// while speaking, Windows itself does not claim it (unlike Win+Space,
    /// which switches keyboard layout), and Space is a real virtual key, which
    /// the release poll requires — a binding whose trigger is a modifier has
    /// no key state to watch.
    /// </para>
    /// <para>
    /// <c>RegisterHotKey</c> is first-come-first-served process-wide, so if
    /// another app on the demo box already owns this combination the
    /// registration is refused and push-to-talk stays off. That is a thing to
    /// check on the machine, not something this code can decide.
    /// </para>
    /// </remarks>
    public static readonly HotkeyBinding PushToTalk =
        new(ModControl | ModAlt | ModNoRepeat, VkSpace, "Ctrl + Alt + Space");

    /// <summary>Virtual key for the digit <paramref name="digit"/> (1-9).</summary>
    public const uint Vk1 = 0x31;

    /// <summary>
    /// workflows D-01 — the keyboard fallback for one workflow.
    /// <c>Ctrl+Alt+Shift+&lt;digit&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shift distinguishes it from the push-to-talk chord, and a digit rather
    /// than a letter because the digits are the one row Windows and the common
    /// apps mostly leave alone.
    /// </para>
    /// <para>
    /// This exists because a demo cannot depend on a recognizer hearing
    /// correctly in a room with an audience in it. It is also how every
    /// workflow is testable on a machine with no working speech stack at all.
    /// </para>
    /// </remarks>
    public static HotkeyBinding Fallback(int digit) => digit is < 1 or > 9
        ? throw new ArgumentOutOfRangeException(nameof(digit), digit, "Fallback keys are 1-9.")
        : new(
            ModControl | ModAlt | ModShift | ModNoRepeat,
            Vk1 + (uint)(digit - 1),
            $"Ctrl + Alt + Shift + {digit}");
}

/// <summary>
/// The Win32 half of a hotkey, as the four operations the state machine needs.
/// Implemented for real by <c>Dictation.Infrastructure</c>'s
/// <c>LowLevelHoldKeyRegistration</c>, and by fakes in the test projects.
/// </summary>
public interface IHotkeyRegistration
{
    /// <summary>The combination this registration is for.</summary>
    HotkeyBinding Binding { get; }

    /// <summary>
    /// Claim the combination process-wide. <c>false</c> when something else
    /// already owns it — a normal outcome, not an error.
    /// </summary>
    bool Register();

    /// <summary>Release the claim. Called once, and only after a successful
    /// <see cref="Register"/>.</summary>
    void Unregister();

    /// <summary>
    /// Whether <see cref="HotkeyBinding.VirtualKey"/> is physically down right
    /// now. This is how the release edge is found; Windows does not send one.
    /// </summary>
    bool IsKeyDown();
}

/// <summary>
/// Push-to-talk: hold the hotkey, talk, let go.
/// </summary>
public interface IPushToTalkHotkey : IDisposable
{
    /// <summary>The user has just started holding the key.</summary>
    event EventHandler? Pressed;

    /// <summary>
    /// The user has let go — or the app is shutting down with the key still
    /// down. Every <see cref="Pressed"/> is followed by exactly one of these.
    /// </summary>
    event EventHandler? Released;

    /// <summary>Whether the key is being held right now.</summary>
    bool IsHeld { get; }

    /// <summary>Whether the combination was actually claimed.</summary>
    bool IsRegistered { get; }

    /// <summary>
    /// Claim the hotkey. Idempotent; returns whether the hotkey is registered
    /// afterwards, so a <c>false</c> means push-to-talk is unavailable.
    /// </summary>
    bool Start();

    /// <summary>
    /// Called by the host when a <c>WM_HOTKEY</c> for this binding arrives.
    /// </summary>
    void NotifyHotkeyMessage();

    /// <summary>
    /// Called by the host on a timer while the app runs. Cheap, and the only
    /// way the release is ever seen.
    /// </summary>
    void PollForRelease();
}

/// <summary>
/// workflows P-02 — the push-to-talk state machine.
///
/// <para>
/// <c>RegisterHotKey</c> gives one edge and not the other: a <c>WM_HOTKEY</c>
/// on the way down, silence on the way up. So the release is derived by asking
/// the key whether it is still down, on a timer the host owns. Everything that
/// makes that trustworthy — a repeat message not opening a second recording, a
/// tick not closing the same one twice, a shutdown mid-sentence still closing
/// it — is here rather than in the Windows adapter, so it is provable on any
/// machine. <c>Dictation.Platform</c> is left with the two P/Invokes.
/// </para>
///
/// <para>
/// Not thread-safe, and deliberately so: both entry points are driven from the
/// WPF message pump on the UI thread, which is the only thread that may touch
/// a hotkey registered against that window's handle.
/// </para>
/// </summary>
public sealed class PushToTalkController : IPushToTalkHotkey
{
    private readonly IHotkeyRegistration _registration;
    private readonly ILogger<PushToTalkController> _logger;

    private bool _disposed;

    /// <summary>
    /// How long the key has been down, so the release can say it.
    /// </summary>
    /// <remarks>
    /// The hold duration is the number the user is certain about — "I held it
    /// for about four seconds" — and until this existed it was nowhere in the
    /// log, so a hold that produced 0.3 s of audio could not be told apart
    /// from a hold the user tapped. It is measured here, at the two edges the
    /// state machine owns, rather than downstream where the microphone's own
    /// timing is mixed in.
    /// </remarks>
    private readonly System.Diagnostics.Stopwatch _held = new();

    public PushToTalkController(
        IHotkeyRegistration registration,
        ILogger<PushToTalkController> logger)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler? Pressed;

    public event EventHandler? Released;

    public bool IsHeld { get; private set; }

    public bool IsRegistered { get; private set; }

    public bool Start()
    {
        if (_disposed || IsRegistered)
        {
            return IsRegistered;
        }

        IsRegistered = _registration.Register();

        if (IsRegistered)
        {
            _logger.LogInformation(
                "Push-to-talk hotkey registered: {Binding}",
                _registration.Binding.Display);
        }
        else
        {
            // Not an exception: another app owning the combination costs the
            // demo its microphone trigger, and the keyboard fallback (D-01) is
            // there for exactly this. An app that refuses to start over a
            // hotkey would cost the demo everything.
            _logger.LogWarning(
                "Push-to-talk hotkey {Binding} is unavailable; voice trigger is off",
                _registration.Binding.Display);
        }

        return IsRegistered;
    }

    public void NotifyHotkeyMessage()
    {
        if (_disposed || !IsRegistered || IsHeld)
        {
            return;
        }

        IsHeld = true;
        _held.Restart();

        // INF, not DBG, and it names the binding. This is the anchor every
        // other line in a hold is read against: without a press line in the
        // log at the level a support bundle keeps, "I pressed the key and
        // nothing happened" produces a trace that is byte-identical to never
        // having pressed it. macOS logs nothing at all here, and that is the
        // single largest hole in its dictation trace.
        _logger.LogInformation(
            "Push-to-talk pressed ({Binding})", _registration.Binding.Display);

        Pressed?.Invoke(this, EventArgs.Empty);
    }

    public void PollForRelease()
    {
        if (_disposed || !IsHeld || _registration.IsKeyDown())
        {
            return;
        }

        IsHeld = false;
        _held.Stop();

        _logger.LogInformation(
            "Push-to-talk released after {HoldMs} ms ({Binding})",
            _held.ElapsedMilliseconds,
            _registration.Binding.Display);

        Released?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Close an open hold before tearing anything down. A Pressed with no
        // Released is a capture nobody stops (P-03).
        if (IsHeld)
        {
            IsHeld = false;
            _held.Stop();

            // Distinguished from an ordinary release, because it is not one:
            // the user never let go, the app went away underneath them. A
            // shutdown mid-sentence and a released key produce the same
            // downstream events, and only this line says which happened.
            _logger.LogInformation(
                "Push-to-talk closed by shutdown with {Binding} still held after {HoldMs} ms",
                _registration.Binding.Display,
                _held.ElapsedMilliseconds);

            Released?.Invoke(this, EventArgs.Empty);
        }

        if (IsRegistered)
        {
            IsRegistered = false;
            _registration.Unregister();
            _logger.LogInformation(
                "Push-to-talk hotkey unregistered: {Binding}",
                _registration.Binding.Display);
        }
    }
}
