using System.Diagnostics;
using System.Runtime.InteropServices;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Infrastructure;

/// <summary>
/// dictation M1 — hold-to-talk on a SINGLE key, including a bare modifier.
///
/// <para>
/// <b>Why this exists at all.</b> <c>RegisterHotKey</c> cannot express what
/// dictation actually needs: it requires a non-modifier virtual key, so a
/// binding built on it becomes something like Ctrl + Alt + Shift + Space —
/// four keys held down while you speak. That is not a shortcut anyone holds through a sentence, and macOS
/// does not ask them to: it holds a single Option.
/// </para>
///
/// <para>
/// A low-level keyboard hook sees every key transition before the focused
/// application does, so a lone Alt, Ctrl, Shift or a function key can be a
/// hold trigger. That is the whole reason to take on a hook, which is
/// otherwise the heavier mechanism.
/// </para>
///
/// <para>
/// <b>It observes, and swallows nothing but a hands-free stop key.</b> The
/// trigger key always reaches the focused app exactly as before. Eating it
/// would be worse than the four-key chord it replaces: a user who binds
/// dictation to Alt and then loses every Alt+Tab has a broken computer, not a
/// shortcut. The cost is that the key keeps its normal meaning too, which is
/// why a bare modifier is the sensible default rather than a letter.
/// </para>
///
/// <para>
/// The one exception, in <see cref="HoldKeyTriggerMode.HoldOrDoubleTapHandsFree"/>
/// only: while a hands-free latch records, Space and Enter (keypad Enter is
/// the same virtual key) end it, and the hook consumes that key's down, its
/// autorepeats and its matching up instead of calling <c>CallNextHookEx</c>
/// (<see cref="HoldKeyTransition.Swallow"/>, <see cref="Consumes"/>), so the
/// stop does not type a space or a newline, or send a half-written message,
/// into the field the words are about to land in. Every other key, and these
/// two at any other moment, pass through. A key sent to a window running at a
/// higher integrity level never reaches this hook at all (UIPI), so there
/// neither the trigger nor the stop keys are seen.
/// </para>
///
/// <para>
/// The mouse hook, installed only when a side button is bound, is the
/// opposite: it swallows that button's press and the release of a press it
/// saw, because Back or Forward underneath a dictation would take the user
/// away from the field (see <c>MouseHookProc</c>).
/// </para>
///
/// <para>
/// <b>Threading.</b> <c>SetWindowsHookEx(WH_KEYBOARD_LL)</c> delivers on the
/// thread that installed it, and only while that thread pumps messages — so
/// this must be constructed and registered from the WPF dispatcher thread, the
/// same constraint <c>UnregisterHotKey</c> has. The callback runs on that
/// thread inside the
/// system's input path: it does the minimum possible work — compare a virtual
/// key code, set a bool — and never allocates, logs or calls out. Windows
/// silently drops a hook whose callback overruns
/// <c>LowLevelHooksTimeout</c> (300 ms by default), and a dropped hook looks
/// exactly like a shortcut that stopped working.
/// </para>
///
/// <para>
/// <b>The delegate is held in a field.</b> A hook proc passed as a lambda is
/// collected while Windows still holds the pointer, and the process dies in
/// the input path with no stack that names this file. Same reason the
/// <c>HwndSourceHook</c> in <c>DictationHotkeyHost</c> is a field.
/// </para>
/// </summary>
public sealed partial class LowLevelHoldKeyRegistration : IHotkeyRegistration, IDisposable
{
    private readonly ILogger<LowLevelHoldKeyRegistration> _logger;

    /// <summary>
    /// Every decision this hook makes, in a type with no Win32 in it.
    /// </summary>
    /// <remarks>
    /// The held flag, the one-phantom-per-hold rule, the bare-Alt branch and
    /// the close-an-open-hold-on-teardown rule all live in
    /// <see cref="HoldKeyState"/> now, so they can be driven from a test.
    /// What is left here is marshalling: read a virtual key out of an
    /// <c>lParam</c>, hand it and the message over, and act on the answer.
    /// That is the same split as <see cref="MouseHoldOrClickState"/> for the
    /// mouse button, for the same reason — this is where every dictation
    /// begins, and a regression here removes the feature rather than
    /// degrading it.
    /// </remarks>
    private readonly HoldKeyState _state;

    private LowLevelKeyboardProc? _proc;
    private IntPtr _hook = IntPtr.Zero;
    private bool _disposed;

    /// <summary>
    /// Set by the hook when Escape landed on a running dictation, or a chord
    /// ended a hold; cleared by the first <see cref="ConsumeCancelRequest()"/>
    /// that sees it. Which of the two it was is the value: see
    /// <see cref="CancelByEscape"/> and <see cref="CancelByChord"/>.
    /// </summary>
    /// <remarks>
    /// An <c>int</c> with <see cref="Volatile"/> rather than a <c>bool</c>, so
    /// the read-and-clear can be one <see cref="Interlocked.Exchange(ref int, int)"/>
    /// — the hook thread and the poll thread are the same thread in the shipped
    /// composition, and this does not depend on that staying true.
    /// </remarks>
    private int _cancelRequested;

    /// <summary>The <see cref="_cancelRequested"/> value Escape latches.</summary>
    private const int CancelByEscape = 1;

    /// <summary>The <see cref="_cancelRequested"/> value a chord latches.</summary>
    private const int CancelByChord = 2;

    /// <summary>The <see cref="_cancelRequested"/> value an Escape latches
    /// that only asks (<see cref="EscapeAsksFirst"/>): nothing is cancelled.</summary>
    private const int CancelAskedByEscape = 3;

    /// <param name="binding">
    /// The trigger. Only <see cref="HotkeyBinding.VirtualKey"/> is used to
    /// decide the hold; modifiers are carried for display, because this
    /// mechanism is deliberately a SINGLE key rather than a chord. A binding
    /// whose VirtualKey is 0 is rejected at construction rather than silently
    /// never firing.
    /// </param>
    /// <param name="triggerMode">
    /// Whether a double-tap latches a dictation open with the key released.
    /// Defaults to hold-only — so a caller that has not been taught about the
    /// toggle gets the plainest gesture. The app passes
    /// <see cref="HoldKeyTriggerMode.HoldOrDoubleTapHandsFree"/>.
    /// </param>
    /// <param name="toggleVirtualKey">
    /// A SECOND key whose double-tap latches a dictation, leaving
    /// <paramref name="binding"/> to do nothing but hold. Zero — the default —
    /// keeps the shipped gesture, where the toggle rides the hold key.
    /// </param>
    /// <param name="mouseButton">
    /// A spare mouse button that ALSO starts a dictation, or
    /// <see cref="DictationMouseButton.None"/> — the default, and the
    /// behaviour every install has had.
    /// </param>
    public LowLevelHoldKeyRegistration(
        HotkeyBinding binding,
        ILogger<LowLevelHoldKeyRegistration> logger,
        HoldKeyTriggerMode triggerMode = HoldKeyTriggerMode.HoldOnly,
        uint toggleVirtualKey = 0,
        DictationMouseButton mouseButton = DictationMouseButton.None,
        bool cancelOnChord = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Binding = binding;

        if (binding.VirtualKey == 0)
        {
            // Kept as an ArgumentException naming `binding`, not forwarded from
            // HoldKeyState's ArgumentOutOfRangeException: the caller's mistake
            // is the binding it passed, not an integer it never typed.
            throw new ArgumentException(
                "A hold trigger needs a virtual key; a binding with none can never fire.",
                nameof(binding));
        }

        _state = new HoldKeyState(
            (int)binding.VirtualKey, triggerMode, (int)toggleVirtualKey, cancelOnChord);
        MouseButton = mouseButton;
        _mouse = mouseButton == DictationMouseButton.None ? null : new MouseHoldOrClickState();
    }

    /// <summary>
    /// The mouse button that also opens a hold, or
    /// <see cref="DictationMouseButton.None"/>.
    /// </summary>
    public DictationMouseButton MouseButton { get; }

    /// <summary>
    /// The mouse gesture, or <c>null</c> when no mouse button is bound.
    /// </summary>
    /// <remarks>
    /// A SECOND machine rather than a mode on <see cref="HoldKeyState"/>, and
    /// the reason is that type's own: it latches on a DOUBLE tap because its
    /// triggers are bare modifiers people brush all day. A spare side button is
    /// pressed for no other reason, so a single click is safe there — and
    /// giving one machine both gestures would put the dangerous one a boolean
    /// away from the safe one.
    /// </remarks>
    private readonly MouseHoldOrClickState? _mouse;

    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook = IntPtr.Zero;

    /// <inheritdoc />
    public HotkeyBinding Binding { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Unlike <c>RegisterHotKey</c> this cannot be refused by another process —
    /// hooks stack, they do not claim. It returns <c>false</c> only when
    /// Windows would not install the hook at all, which is a machine-policy
    /// failure rather than a collision, and is logged with the Win32 error so
    /// the two are distinguishable in the field.
    /// </remarks>
    public bool Register()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != IntPtr.Zero)
        {
            return true;
        }

        _proc = HookProc;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);

        if (_hook == IntPtr.Zero)
        {
            _proc = null;
            _logger.LogWarning(
                "Hold-to-talk hook was refused (error {Error}); dictation is unavailable this run.",
                Marshal.GetLastWin32Error());
            return false;
        }

        // The SECOND hook, and only when a button is bound. A WH_MOUSE_LL hook
        // is not free: it sees WM_MOUSEMOVE on every pointer motion, hundreds a
        // second, on the same system input path Windows unhooks for overrunning
        // LowLevelHooksTimeout. Installing one for a feature the user has not
        // asked for would be a background cost for nothing.
        if (_mouse is not null && ObserveMouseHookOutcome(RegisterMouse()))
        {
            // NOT fatal to the keyboard trigger, deliberately. A refused mouse
            // hook must not take hold-to-talk down with it — the user still has
            // the key they have been using all along, and the log names what
            // they lost.
            //
            // A log line is all this used to be, and a log line is not a
            // surface: the user had bound a button, nothing happened when they
            // pressed it, and the pane went on rendering the picker as though
            // the binding were live. MouseTriggerDegraded is now set above so
            // the pane can say so — macOS surfaces the same fact as an amber
            // line under its own mouse row.
            _logger.LogWarning(
                "The mouse trigger ({Button}) could not be installed; the keyboard trigger is "
                + "unaffected.", MouseButton);
        }

        _armedTicks = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _observedEvents, 0);

        _logger.LogInformation("Hold-to-talk armed on {Binding}", Binding.Display);
        return true;
    }

    private bool RegisterMouse()
    {
        _mouseProc = MouseHookProc;
        using var module = Process.GetCurrentProcess().MainModule!;
        _mouseHook = SetWindowsHookEx(
            WH_MOUSE_LL, _mouseProc, GetModuleHandle(module.ModuleName), 0);

        if (_mouseHook == IntPtr.Zero)
        {
            _mouseProc = null;
            return false;
        }

        _logger.LogInformation("Mouse trigger armed on {Button}", MouseButton);
        return true;
    }

    /// <summary>
    /// Whether a mouse button is bound and its <c>WH_MOUSE_LL</c> hook is NOT
    /// installed — the button the user picked does nothing, and only this says
    /// so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the state a refused mouse hook used to leave behind silently.
    /// <see cref="Register"/> returns <c>true</c> on that path, deliberately —
    /// see the comment there: the keyboard trigger is unaffected and must not
    /// be taken down with it — so the shell had no way to tell the difference
    /// between "button 4 is armed" and "button 4 was refused and does nothing".
    /// The user's report is "I bound a button and nothing happens", which is
    /// indistinguishable from a wrong binding, a dead mouse, or the feature
    /// never having shipped.
    /// </para>
    /// <para>
    /// <b>False when no button is bound.</b> Not binding one is the shipped
    /// default and is not a degradation; a property that read true for every
    /// install would put an amber fault line under a row nobody had touched.
    /// </para>
    /// <para>
    /// <b>False before the arm is attempted, and again after
    /// <see cref="Unregister"/>.</b> A registration that has not tried yet has
    /// not failed, and a torn-down one is not degraded — it is off. Only the
    /// span between an attempted arm and its teardown can carry a fault.
    /// </para>
    /// </remarks>
    public bool MouseTriggerDegraded => Volatile.Read(ref _mouseDegraded) != 0;

    /// <summary>
    /// Set once <see cref="Register"/> has attempted the mouse hook and been
    /// refused; cleared by <see cref="Unregister"/>. An <c>int</c> with
    /// <see cref="Volatile"/> for the same reason <see cref="_cancelRequested"/>
    /// is one — the shell reads this from the dispatcher and this type does not
    /// depend on that being the only thread that ever will.
    /// </summary>
    private int _mouseDegraded;

    /// <summary>
    /// The degrade rule, reachable without a hook: record whether the
    /// <c>WH_MOUSE_LL</c> arm succeeded and answer what
    /// <see cref="MouseTriggerDegraded"/> now reads.
    /// </summary>
    /// <param name="installed">Whether <c>SetWindowsHookEx</c> gave us a hook.</param>
    /// <returns>The new value of <see cref="MouseTriggerDegraded"/>.</returns>
    /// <remarks>
    /// <b>Public for exactly the reason <see cref="ObserveMouse"/> is.</b>
    /// <see cref="Register"/> is <c>SetWindowsHookEx</c> and needs a live input
    /// stack no test host can offer, and the one outcome that matters here is
    /// the one a developer machine essentially never produces — Windows
    /// refusing the hook. Without this seam "a refused mouse hook shows the
    /// user a degrade line" would be a claim nobody could check, and the whole
    /// surface could be deleted without a test going red. It records an outcome
    /// rather than inventing one: the caller in <see cref="Register"/> hands it
    /// the real answer from <see cref="RegisterMouse"/>.
    /// </remarks>
    public bool ObserveMouseHookOutcome(bool installed)
    {
        var degraded = MouseButton != DictationMouseButton.None && !installed;
        Volatile.Write(ref _mouseDegraded, degraded ? 1 : 0);
        return degraded;
    }

    /// <inheritdoc />
    public void Unregister()
    {
        // The STATE is cleared unconditionally, above the hook check.
        //
        // Only the unhook needs a hook. Everything below is this object's own
        // idea of what is happening, and a caller that asked for teardown is
        // entitled to have it emptied whether or not Windows ever accepted the
        // hook — the machine-policy refusal path in Register leaves exactly
        // that shape behind.
        //
        // Deliberate: a hold that was open when the hook came down is closed,
        // so a teardown mid-utterance cannot leave the caller believing the key
        // is still pressed and the microphone still wanted.
        _state.Release();
        // The mouse gesture with it, including a latched dictation. A latch
        // that outlived its own hook would leave IsKeyDown answering true with
        // nothing able to ever set it false.
        _mouse?.Release();
        // And the degrade flag, above the hook check with the rest of the
        // state. A torn-down trigger is OFF, not degraded — leaving it set
        // would keep an amber "the mouse button is doing nothing" line under a
        // pane after the user switched dictation off, and Rearm's fresh
        // registration would then be arguing with a stale one.
        Volatile.Write(ref _mouseDegraded, 0);
        // And the cancel latch with it. A pending Escape that outlived its own
        // dictation would be consumed by the NEXT one — a re-arm cancelling the
        // first thing the user says after it.
        Interlocked.Exchange(ref _cancelRequested, 0);
        _state.DictationInFlight = false;

        if (_hook == IntPtr.Zero)
        {
            return;
        }

        // The one line that can say the hook was deaf. Zero events over an arm
        // that lasted minutes is not a user who never typed — it is this
        // process being unable to observe the input path at all, and it is the
        // only evidence that distinguishes that from a wrong binding.
        //
        // The duration is on BOTH branches, because zero events only means
        // anything against the span it is zero over.
        var observed = Volatile.Read(ref _observedEvents);
        var armedFor = _armedTicks == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_armedTicks);

        if (observed == 0 && armedFor >= DeafnessIsMeaningfulAfter)
        {
            _logger.LogWarning(
                "Hold-to-talk on {Binding} is coming down having observed NO keyboard events in "
                + "{ArmedS:F0}s of being armed. The hook was installed and Windows never delivered "
                + "anything through it, so no press could have been seen — this is not a user who "
                + "did not press the key. A window running at a higher integrity level than this app, "
                + "or a policy on this machine, is the usual cause",
                Binding.Display,
                armedFor.TotalSeconds);
        }
        else
        {
            _logger.LogInformation(
                "Hold-to-talk on {Binding} is coming down having observed {Events} keyboard "
                + "event(s) in {ArmedS:F0}s of being armed",
                Binding.Display,
                observed,
                armedFor.TotalSeconds);
        }

        Interlocked.Exchange(ref _observedEvents, 0);
        _armedTicks = 0;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }
    }

    /// <summary>
    /// How many keyboard events this hook has observed since it was installed.
    /// </summary>
    /// <remarks>
    /// Not a keylogger and deliberately not one: it is a COUNT, with no key,
    /// no character and no timing kept. Its only job is to separate "the hook
    /// is deaf" from "the user pressed a different key", which are the two
    /// halves of "I pressed it and nothing happened" and were previously the
    /// same empty log.
    /// </remarks>
    public int ObservedKeyEvents => Volatile.Read(ref _observedEvents);

    private int _observedEvents;

    /// <summary>When the current hook was installed, for the deafness check.</summary>
    private long _armedTicks;

    /// <summary>
    /// How long a hook has to have been armed before observing nothing is
    /// evidence of anything.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed at: this shell arms, disarms and re-arms during
    /// startup — on the device the whole dance took 176 ms — and a hook that
    /// lived for a fifth of a second has not been ignored by Windows, it has
    /// simply not been alive while anybody typed. The first draft of the
    /// warning below fired on that startup teardown and was pure noise, which
    /// is the thing this whole change is meant not to add. Thirty seconds is
    /// far past every re-arm the shell performs and far short of any session in
    /// which a user would reach for the key.
    /// </remarks>
    private static readonly TimeSpan DeafnessIsMeaningfulAfter = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public bool IsKeyDown() => _state.IsHeld || (_mouse?.IsHeld ?? false);

    /// <summary>
    /// End a latched hold, or a short press's parked hold, from outside the
    /// keyboard stream: the recording limit or a silence stop, Cancel, a press
    /// the shell refused, or a latch the shell found with no dictation behind
    /// it. The shell and the hook share one latch flag, and a stale one would
    /// keep the microphone open and Space and Enter swallowed. Runs on the
    /// thread that installed the hook, where every other mutation of the
    /// gesture lives.
    /// See <see cref="HoldKeyState.EndLatch"/>.
    /// </summary>
    public void EndLatch()
    {
        _state.EndLatch();
        _mouse?.EndLatch();
    }

    /// <summary>
    /// Which key ended the last keyboard latch, once; see
    /// <see cref="HoldKeyState.ConsumeLatchEnd"/>. The mouse gesture's latch
    /// is not reported here.
    /// </summary>
    public LatchEnd ConsumeLatchEnd() => _state.ConsumeLatchEnd();

    /// <summary>
    /// Whether the hook consumes the event <paramref name="transition"/> was
    /// decided for — returns non-zero without calling <c>CallNextHookEx</c>.
    /// True only for <see cref="HoldKeyTransition.Swallow"/>, which the
    /// hands-free stop keys produce while a latch records, and the bound
    /// mouse button's press and tracked release (<see cref="ObserveMouse"/>).
    /// </summary>
    /// <remarks>Public and static so the rule <c>HookProc</c> and
    /// <c>MouseHookProc</c> act on can be asserted without a hook, as
    /// <see cref="ObserveKeyboard"/> is.</remarks>
    public static bool Consumes(HoldKeyTransition transition) =>
        (transition & HoldKeyTransition.Swallow) != 0;

    /// <summary>
    /// Whether a dictation is latched open with the trigger released — true
    /// only between the double-tap that started it and whatever ended it (see
    /// <see cref="HoldKeyState.IsLatched"/>), and always false in hold-only
    /// mode.
    /// </summary>
    public bool IsLatched => _state.IsLatched || (_mouse?.IsLatched ?? false);

    /// <summary>
    /// Whether the mouse button is what holds the dictation open right now:
    /// pressed, or latched by a click. False with no button bound.
    /// </summary>
    /// <remarks>The window and the indicator name the control the user is
    /// actually holding with this; <see cref="IsKeyDown"/> answers for both
    /// at once.</remarks>
    public bool MouseHolds => _mouse?.IsHeld ?? false;

    /// <summary>
    /// Whether a click on the mouse button latched the dictation. Its stop is
    /// another press of the button: Space, Enter and the hold key end only
    /// the keyboard's own latch.
    /// </summary>
    public bool MouseLatched => _mouse?.IsLatched ?? false;

    /// <summary>
    /// Whether the most recently completed KEY hold was chorded — another key
    /// pressed during it, or an AltGr-synthesised press. The tap-toggle latch
    /// consults this and refuses to latch on typing. A mouse hold never
    /// chords, so the mouse gesture contributes nothing here.
    /// </summary>
    public bool LastHoldWasChord => _state.LastHoldWasChord;

    /// <summary>
    /// Tell the hook whether Escape means anything to it now: a dictation is
    /// listening or transcribing, or (in the standalone app) a press is
    /// waiting for its password-field check.
    /// </summary>
    /// <remarks>
    /// Pushed down from the shell, which is the only layer that can see the
    /// session. Setting it <c>false</c> is what makes Escape inert again the
    /// instant a dictation finishes — including one that finished because
    /// Escape cancelled it.
    /// </remarks>
    public bool DictationInFlight
    {
        get => _state.DictationInFlight;
        set => _state.DictationInFlight = value;
    }

    /// <summary>
    /// Read and clear the "Escape was pressed on a running dictation" latch.
    /// </summary>
    /// <returns>
    /// <c>true</c> exactly once per Escape press, on the first caller to ask
    /// after it happened.
    /// </returns>
    /// <remarks>
    /// Read-and-clear rather than an event, because the raise would have to
    /// happen inside the hook callback. See <see cref="HookProc"/>: everything
    /// a cancel actually does is dispatcher work, and a hook proc that does
    /// dispatcher work gets the hook removed by Windows and dictation with it.
    /// Exactly-once matters — a latch that stayed set would cancel the NEXT
    /// dictation the moment it started.
    /// </remarks>
    public bool ConsumeCancelRequest() => ConsumeCancelRequest(out _);

    /// <summary>
    /// <see cref="ConsumeCancelRequest()"/>, and whether the cancel was a chord
    /// rather than Escape.
    /// </summary>
    /// <param name="byChord">
    /// True when another key pressed during the hold ended it (the
    /// <c>cancelOnChord</c> option). Decided here, from the key that asked,
    /// rather than read from <see cref="LastHoldWasChord"/>: that verdict is
    /// published at the trigger's key-up, so at the moment the cancel is
    /// consumed — the key usually still down — it still describes the
    /// PREVIOUS hold.
    /// </param>
    public bool ConsumeCancelRequest(out bool byChord) =>
        ConsumeCancelRequest(out byChord, out var asked) && !asked;

    /// <summary>
    /// <see cref="ConsumeCancelRequest(out bool)"/>, and whether the Escape
    /// only asked (<see cref="EscapeAsksFirst"/>): then it returns true with
    /// <paramref name="asked"/> set, and nothing is to be cancelled. The
    /// one-argument read treats an asking Escape as nothing at all.
    /// </summary>
    public bool ConsumeCancelRequest(out bool byChord, out bool asked)
    {
        var latched = Interlocked.Exchange(ref _cancelRequested, 0);
        byChord = latched == CancelByChord;
        asked = latched == CancelAskedByEscape;
        return latched != 0;
    }

    /// <summary>
    /// Whether the next Escape on a running dictation only asks: see
    /// <see cref="HoldKeyState.EscapeAsksFirst"/>. The Dictation app raises it
    /// while its take holds more than thirty seconds of speech.
    /// </summary>
    public bool EscapeAsksFirst
    {
        get => _state.EscapeAsksFirst;
        set => _state.EscapeAsksFirst = value;
    }

    /// <summary>
    /// loop-v2 keyboard-origin feed (the span-observer contract §5.1). Called for every
    /// key-down the hook sees with the unix-ms timestamp and the pid of the
    /// foreground window at that instant — <b>timestamps and pids only, never
    /// key codes</b> (the Activity contract's "no keystroke content anywhere").
    /// <c>null</c> — the default — costs nothing in the hook: no foreground
    /// lookup, no call. The host sets it only when <c>feature.dictation_learning_v2</c>
    /// built a <c>LearningHost</c>; the hook itself knows nothing about observers.
    /// </summary>
    public Action<long, int>? KeyboardInputObserved
    {
        get => Volatile.Read(ref _keyboardInputObserved);
        set => Volatile.Write(ref _keyboardInputObserved, value);
    }

    private Action<long, int>? _keyboardInputObserved;

    /// <summary>
    /// The forwarding rule, public so it can be asserted without a hook: a
    /// key-down (<see cref="HoldKeyState.WmKeyDown"/> / <see cref="HoldKeyState.WmSysKeyDown"/>)
    /// reaches <see cref="KeyboardInputObserved"/> as <c>(nowMs, foregroundProcessId)</c>;
    /// key-ups and a missing sink are no-ops. Nothing about <em>which</em> key
    /// is passed through.
    /// </summary>
    /// <returns><c>true</c> when the sink was called.</returns>
    public bool ForwardKeyboardInput(int message, long nowMs, int foregroundProcessId)
    {
        var sink = KeyboardInputObserved;
        if (sink is null || !IsKeyDownMessage(message))
        {
            return false;
        }
        sink(nowMs, foregroundProcessId);
        return true;
    }

    /// <summary><c>WM_KEYDOWN</c> or <c>WM_SYSKEYDOWN</c>.</summary>
    public static bool IsKeyDownMessage(int message) =>
        message is HoldKeyState.WmKeyDown or HoldKeyState.WmSysKeyDown;

    /// <summary>Pid of the foreground window, or 0 when there is none.</summary>
    public static int ForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return 0;
        }
        _ = GetWindowThreadProcessId(window, out var pid);
        return (int)pid;
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        // Minimum possible work: overrunning LowLevelHooksTimeout gets the
        // hook silently removed. The one early return is a consumed stop key,
        // and it is cheaper than the call it skips.
        //
        // Marshalling and one call. Every rule this used to spell out inline —
        // when a hold opens, when it closes, and the once-per-hold phantom that
        // only a bare Alt needs — is HoldKeyState's now, where a test can reach
        // it. What has to stay here is the injection itself, because SendInput
        // is exactly the thing that type must not contain.
        if (code >= 0)
        {
            // One interlocked increment, and no logging, ever, on this thread.
            // The count is what answers "the hook is installed but is it
            // SEEING anything" — a hook that Windows accepted and that receives
            // no events (a higher-integrity foreground window, UIPI, an input
            // path this process cannot observe) is silent in exactly the same
            // way as a user who never pressed the key. That case has already
            // been hit here: an injected keystroke at medium integrity into
            // this elevated app produced literally zero lines. The dispatcher
            // reads and reports this; the hook only counts.
            Interlocked.Increment(ref _observedEvents);

            var virtualKey = Marshal.ReadInt32(lParam);
            var transition = ObserveKeyboardEvent(
                virtualKey, (int)wParam,
                unchecked((uint)Marshal.ReadInt32(lParam, KbdLlTimeOffset)), Environment.TickCount64);

            if ((transition & HoldKeyTransition.SuppressMenu) != 0)
            {
                SuppressMenuModeArming();
            }

            if (Consumes(transition))
            {
                // A hands-free stop key: Space or Enter, while a latch
                // records. Consumed, so the focused app never sees it; and not
                // reported to the keyboard-origin feed below, because it was
                // not an edit to anything.
                return ConsumedEvent;
            }

            // loop-v2 keyboard-origin feed: two cheap user32 calls and one
            // delegate, and only while a sink is attached. No key code leaves
            // this method; see ForwardKeyboardInput. Never the unassigned
            // VK 0xE8, which no keyboard sends: this hook injects it (the bare
            // Alt phantom, the hook watch's test key), and passed on it would
            // read as the user typing.
            if (_keyboardInputObserved is not null && virtualKey != HoldKeyState.VkMenuSuppression)
            {
                ForwardKeyboardInput(
                    (int)wParam, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ForegroundProcessId());
            }
        }

        // Everything else. See the class remarks: this observes.
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>What a low-level hook, keyboard or mouse, returns to consume
    /// an event: any non-zero value, without calling <c>CallNextHookEx</c>.</summary>
    private static readonly IntPtr ConsumedEvent = new(1);

    // ─── Event time ─────────────────────────────────────────────────────

    /// <summary>
    /// The byte offset of <c>time</c> in <c>KBDLLHOOKSTRUCT</c>: after
    /// <c>vkCode</c>, <c>scanCode</c> and <c>flags</c>, three DWORDs.
    /// </summary>
    private const int KbdLlTimeOffset = 12;

    /// <summary>
    /// The oldest an event's own time stamp may be, against the moment the
    /// callback runs, and still be used. Comfortably past
    /// <c>LowLevelHooksTimeout</c>, which Windows caps at one second: an
    /// event the system held for this hook that long is one it delivered
    /// late, not one with a time stamp of its own.
    /// </summary>
    public const long MaxEventAgeMs = 2_000;

    /// <summary>The instant the last keyboard event was stamped with. Hook
    /// thread only.</summary>
    private long _lastKeyEventMs;

    /// <summary>
    /// When a keyboard event happened, on the <see cref="Environment.TickCount64"/>
    /// clock, from its own 32-bit time stamp (<c>KBDLLHOOKSTRUCT.time</c>, the
    /// same tick count, wrapping every 49.7 days).
    /// </summary>
    /// <param name="eventTime">The event's <c>time</c>.</param>
    /// <param name="nowMs">The clock when the callback runs.</param>
    /// <param name="lastMs">The instant the previous event was stamped with;
    /// no event is stamped earlier, so the clock the gesture reads never runs
    /// backwards.</param>
    /// <remarks>
    /// <para>
    /// <b>Why not the callback's own clock.</b> The callback runs on the
    /// dispatcher thread, and the dispatcher can be busy when the user acts:
    /// a press opens the microphone there, synchronously, and a cold or
    /// Bluetooth device takes hundreds of milliseconds to open. The events
    /// the user made meanwhile, the first tap's release and the second
    /// press, wait for the thread and arrive together. Timed on arrival, a
    /// 160 ms double-tap could measure past the 350 ms window and not latch,
    /// and the first tap's release would no longer park its hold. Timed when
    /// they happened, the gesture is what the user did.
    /// </para>
    /// <para>
    /// A stamp that is ahead of the clock, or older than
    /// <see cref="MaxEventAgeMs"/>, is not trusted (an injected event can
    /// carry any time) and the callback's clock is used instead. A stall
    /// longer than <c>LowLevelHooksTimeout</c> is beyond this: Windows passes
    /// the event on without this hook, or removes the hook.
    /// </para>
    /// </remarks>
    public static long EventTimeMs(uint eventTime, long nowMs, long lastMs)
    {
        // Modulo 2^32, so a stamp from just before the 32-bit count wrapped
        // still reads as a few milliseconds old.
        var age = unchecked((uint)nowMs - eventTime);
        var at = age <= MaxEventAgeMs ? nowMs - age : nowMs;
        return Math.Max(at, lastMs);
    }

    /// <summary>
    /// <see cref="ObserveKeyboard"/> for one event as the hook receives it:
    /// stamped with when it happened (<see cref="EventTimeMs"/>), not with
    /// when the callback got to run. What <c>HookProc</c> calls.
    /// </summary>
    public HoldKeyTransition ObserveKeyboardEvent(int virtualKey, int message, uint eventTime, long nowMs)
    {
        var at = EventTimeMs(eventTime, nowMs, _lastKeyEventMs);
        _lastKeyEventMs = at;
        return ObserveKeyboard(virtualKey, message, at);
    }

    /// <summary>
    /// Fold one keyboard event into the trigger state and latch a cancel when
    /// it asks for one — everything <c>HookProc</c> does short of the
    /// injection and the time stamp (<see cref="ObserveKeyboardEvent"/>), so a
    /// test can drive the hook's own path.
    /// </summary>
    public HoldKeyTransition ObserveKeyboard(int virtualKey, int message, long nowMs)
    {
        CountTriggerEvent(virtualKey);
        var transition = _state.Observe(virtualKey, message, nowMs);

        if ((transition & HoldKeyTransition.CancelRequested) != 0)
        {
            // A bool, and nothing else. Cancelling a dictation means
            // cancelling a token, transitioning the session, raising an
            // event and repainting the HUD — none of which belongs on a
            // thread Windows will silently unhook for overrunning
            // LowLevelHooksTimeout. The host already ticks a dispatcher
            // timer at 33 Hz for exactly this reason (it is how the
            // RELEASE is seen too), so the latch is picked up within one
            // tick by the thread that can afford the work.
            Volatile.Write(
                ref _cancelRequested,
                virtualKey == HoldKeyState.VkEscape ? CancelByEscape : CancelByChord);
        }
        else if ((transition & HoldKeyTransition.EscapeAsked) != 0)
        {
            // Asked, not cancelled: the same bool, read the same way
            // (ConsumeCancelRequest), and never over a cancel already latched.
            Interlocked.CompareExchange(ref _cancelRequested, CancelAskedByEscape, 0);
        }

        return transition;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
    }

    /// <summary>
    /// The mouse half of the trigger: read the button out of an
    /// <c>MSLLHOOKSTRUCT</c> and hand the edge to
    /// <see cref="MouseHoldOrClickState"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same discipline as <see cref="HookProc"/> and it matters MORE here:
    /// this callback runs on every pointer movement, so the common path has to
    /// be one comparison and a return. <c>EdgeOf</c> answers null for
    /// everything that is not an X-button transition, which is all of it.
    /// </para>
    /// <para>
    /// <b>Unlike the keyboard trigger, the bound button is swallowed.</b> A
    /// bare modifier has to keep its meaning (Alt+Tab, Ctrl+C), but a side
    /// button's meaning is Back or Forward, and a quick click on it latches a
    /// dictation: passed on, every Back would navigate away from the field and
    /// leave the microphone open behind it. Its press, and the release of a
    /// press this hook saw, return non-zero without <c>CallNextHookEx</c>
    /// (<see cref="DictationMouseMessages.Swallows"/>); the caution beside the
    /// picker says what the button stops doing.
    /// </para>
    /// </remarks>
    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            // mouseData sits after the POINT and before dwFlags in
            // MSLLHOOKSTRUCT: 8 bytes of x/y, then 4 of mouseData. Read before
            // the message is examined only because Marshal.ReadInt32 is cheaper
            // than a second branch; ObserveMouse discards it for every message
            // that is not an X-button transition, which is all of them.
            var transition = ObserveMouse(
                (int)wParam, unchecked((uint)Marshal.ReadInt32(lParam, 8)),
                Environment.TickCount64);
            if (Consumes(transition))
            {
                return ConsumedEvent;
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// The decision half of the mouse hook, reachable without one.
    /// </summary>
    /// <param name="message">The <c>WM_</c> the hook was handed.</param>
    /// <param name="mouseData">
    /// <c>MSLLHOOKSTRUCT.mouseData</c>. The side button is in the HIGH word.
    /// </param>
    /// <param name="nowMs">A monotonic millisecond clock.</param>
    /// <returns>
    /// What the gesture decided, if anything, with
    /// <see cref="HoldKeyTransition.Swallow"/> added when the hook must keep
    /// the event (<see cref="DictationMouseMessages.Swallows"/>) — the one part
    /// <c>MouseHookProc</c> acts on, through <see cref="Consumes"/>, as the
    /// keyboard hook does. The poll tick reads <see cref="IsKeyDown"/> for the
    /// rest, the same way it does for keys.
    /// </returns>
    /// <remarks>
    /// <b>Public so the wiring can be asserted.</b> <c>MouseHookProc</c> is
    /// only ever called by Windows from inside the system input path, which no
    /// test host can reach — the same limitation this class's own remarks
    /// record for <c>HookProc</c>. Everything above this line is marshalling;
    /// everything below is the gesture. Without this seam, "a synthesised
    /// XBUTTON1 press opens a hold" would be a claim nobody could check, and
    /// the mouse arm could be deleted without a single test going red.
    /// </remarks>
    public HoldKeyTransition ObserveMouse(int message, uint mouseData, long nowMs)
    {
        if (_mouse is null)
        {
            return HoldKeyTransition.None;
        }

        var edge = DictationMouseMessages.EdgeOf(message);
        if (edge is null)
        {
            return HoldKeyTransition.None;
        }

        if (DictationMouseMessages.ButtonOf(mouseData) != MouseButton)
        {
            // The OTHER side button. Both arrive as the same two messages, so a
            // user who bound button 4 must not have button 5 open a microphone.
            return HoldKeyTransition.None;
        }

        // Decided before the gesture sees the edge: a release is kept only when
        // the gesture had tracked its press, which the edge is about to clear.
        var swallow = DictationMouseMessages.Swallows(message, mouseData, MouseButton, _mouse.IsDown);
        var transition = edge.Value ? _mouse.Down(nowMs) : _mouse.Up(nowMs);
        return swallow ? transition | HoldKeyTransition.Swallow : transition;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    // The four WM_ constants that used to sit here moved to HoldKeyState with
    // the logic that reads them — including the SYSKEY pair a bare Alt produces
    // instead of the ordinary one, whose absence is why an Alt binding would
    // appear dead. One copy, beside the switch that consumes it.

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ── Menu-mode suppression ───────────────────────────────────────────────

    /// <summary>
    /// Break the "nothing was pressed between Alt-down and Alt-up" condition
    /// the target's menu bar arms on, by injecting one press-and-release of
    /// <c>VK 0xFF</c> — the reserved, unassigned virtual key — while the hold
    /// is open. The focused application sees Alt+&lt;nothing meaningful&gt;
    /// instead of a lone Alt tap, so its release arms no menu and the
    /// inserter's Ctrl+V lands in the text box it was aimed at. This is the
    /// established push-to-talk answer (voice chat clients that offer Alt as a
    /// trigger all do it); the alternative — swallowing the trigger in the
    /// hook — would change what every other application sees for the whole
    /// hold, which is a far bigger contract than two inert key events.
    /// </summary>
    /// <remarks>
    /// Called from inside the hook proc, deliberately: the injection must land
    /// between the trigger's down and its up, and a tap can be quicker than a
    /// thread-pool hop. <c>SendInput</c> with two events is microseconds — far
    /// inside the LowLevelHooksTimeout the hook's own remarks worry about. The
    /// injected 0xFF re-enters this hook and is discarded by the trigger-key
    /// check in one comparison.
    /// </remarks>
    private void SuppressMenuModeArming()
    {
        var scan = (ushort)MapVirtualKey(VK_UNASSIGNED, MAPVK_VK_TO_VSC);
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VK_UNASSIGNED;
        inputs[0].U.ki.wScan = scan;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = VK_UNASSIGNED;
        inputs[1].U.ki.wScan = scan;
        inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
        {
            // A refused injection costs menu-mode suppression, not dictation:
            // the hold still opens the microphone. Logged so "paste vanishes
            // in this one app" has a lead in the file.
            _logger.LogWarning(
                "Menu-mode suppression key could not be injected (error {Error}).",
                Marshal.GetLastWin32Error());
        }
    }

    /// <summary><c>VK 0xE8</c> — the unassigned virtual key. NOT 0xFF: 0xFF
    /// is reserved-invalid and the input system drops it without delivering,
    /// which leaves menu-mode arming intact — measured on this device (the
    /// 0xFF injection changed nothing; 0xE8 with its mapped scan code
    /// neutralised the arming in the same harness). 0xE8 is a legal key no
    /// keyboard produces and no application binds.</summary>
    private const ushort VK_UNASSIGNED = 0xE8;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
