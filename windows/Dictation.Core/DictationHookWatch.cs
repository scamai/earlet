namespace Dictation.Core;

// ─── The hook watch: noticing a hold-key hook Windows removed ──────────────
//
// Windows removes a low-level keyboard hook whose callback overruns
// LowLevelHooksTimeout (300 ms by default) and tells nobody; the hook runs on
// the UI thread, which also opens the microphone at a press. The Mac's event
// tap is told when macOS disables it and re-enables itself
// (DictationHotkey.swift handleTapEvent), and a listener that cannot start is
// reported (DictationModel.triggerDegraded): a status line and the window
// brought forward once. Windows is told nothing, so the controller's poll
// compares the hook's view of the hold key with Windows' own and decides
// here, in code with no Win32 in it, whether the hook has gone.

/// <summary>
/// What the hook watch reads from the hold-key hook and asks of it. Part of
/// <see cref="IDictationTrigger"/>; every member runs on the thread that
/// installed the hook, from the controller's poll, never from the callback.
/// </summary>
public interface IHookWatchTarget
{
    /// <summary>The hold key as Windows and as the hook see it, and how many
    /// keyboard events the hook has received.</summary>
    HookReading ReadHook();

    /// <summary>
    /// Send the hook one harmless key (the unassigned <c>VK 0xE8</c> a bare
    /// Alt hold already gets), so a later reading shows whether it still
    /// receives anything. False when Windows refused the key, which then
    /// proves nothing either way (<see cref="DictationHookWatch.TestKeyRefused"/>).
    /// </summary>
    bool ProbeHook();

    /// <summary>
    /// Put the hook's view of the hold key where Windows says it is: a
    /// release it never saw ends the hold now, as a release would; a press it
    /// never saw holds nothing until the key comes up
    /// (<c>HoldKeyState.Resync</c>).
    /// </summary>
    void Resync(bool keyDown);

    /// <summary>Unhook, if Windows has not already, and hook the keyboard
    /// again on the hook's thread. Whether a hook is in place afterwards.</summary>
    bool Reinstall();
}

/// <summary>One look at the hold key from both sides.</summary>
/// <param name="KeyDown">Whether Windows says the key is down
/// (<c>GetAsyncKeyState</c>).</param>
/// <param name="HookSawKeyDown">Whether the hook last saw it go down and not
/// come up.</param>
/// <param name="HookEvents">How many keyboard events the hook has received
/// since it was armed. Only its changes mean anything.</param>
/// <param name="TriggerEvents">How many of those were the hold key itself,
/// its autorepeats included. Only its changes mean anything.</param>
public readonly record struct HookReading(bool KeyDown, bool HookSawKeyDown, long HookEvents, long TriggerEvents = 0)
{
    /// <summary>Whether the two sides disagree about the key.</summary>
    public bool Disagree => KeyDown != HookSawKeyDown;
}

/// <summary>
/// What the window in front runs as. UIPI keeps a low-level hook from seeing
/// keys sent to a window running at a higher integrity level, so with one in
/// front the hold key cannot be heard at all.
/// </summary>
public interface IForegroundElevation
{
    /// <summary>Whether Dictation itself runs elevated: then no ordinary
    /// window is above it, and the question never needs asking.</summary>
    bool ThisProcessElevated { get; }

    /// <summary>The process that owns the window in front, or 0 when there is
    /// none.</summary>
    int ForegroundProcessId();

    /// <summary>Whether that process runs elevated; null when it cannot be
    /// told.</summary>
    bool? IsElevated(int processId);
}

/// <summary>What the hook watch knows about the hold-key hook itself. Only
/// the hook: a window running as administrator in front is the controller's
/// other input, and which of the two the icon shows is
/// <see cref="DictationTrayGlyph"/>'s to decide, with the microphone's.</summary>
public enum HookHealth
{
    /// <summary>Nothing known to be wrong.</summary>
    Healthy,

    /// <summary>The hook was found removed and a new one installed, which has
    /// not received a key yet.</summary>
    Reconnected,

    /// <summary>The hook was found removed and Windows would not install a
    /// new one: the hold key does nothing until the user acts.</summary>
    Unheard,
}

/// <summary>What kind of window is in front, as the hook watch needs to
/// know it (<see cref="AdminWindowWatch"/>).</summary>
public enum WindowInFront
{
    /// <summary>An ordinary window, which the hook hears.</summary>
    Ordinary,

    /// <summary>A window running as administrator, which it cannot (UIPI).</summary>
    Administrator,

    /// <summary>No window: a secure desktop (Ctrl+Alt+Del, a UAC prompt, the
    /// lock screen) or a switch under way. Windows' key state reads up there,
    /// and no key reaches any hook, a test key included.</summary>
    None,

    /// <summary>A window whose process could not be asked what it runs as
    /// (it exited meanwhile, or would not answer). Whether the hook hears it
    /// cannot be told, so the watch treats it as <see cref="None"/>: no test
    /// key and never a loss.</summary>
    Unknown,
}

/// <summary>What one look at the hook found.</summary>
public enum HookFinding
{
    /// <summary>Nothing to act on.</summary>
    None,

    /// <summary>The two sides began to disagree: look again shortly, and,
    /// with <see cref="HookVerdict.Probe"/>, send the hook a test key.</summary>
    Suspected,

    /// <summary>The disagreement lasted, but the hook received keys meanwhile
    /// (the test key, or the user's): it is alive and missed one edge. Put its
    /// view right; nothing else.</summary>
    EdgeMissed,

    /// <summary>The disagreement lasted with a window running as
    /// administrator in front, where the hook hears nothing by design: put
    /// its view right and say why, but do not reinstall.</summary>
    AdminWindow,

    /// <summary>A test key sent to the hook never arrived: Windows removed
    /// it. Put its view right, reinstall it and say so. Decided
    /// <see cref="DictationHookWatch.ConfirmAfter"/> after the test key went
    /// out, whether or not the press that prompted it is over by then.</summary>
    Lost,

    /// <summary>The disagreement lasted and nothing reached the hook, but
    /// Windows refused the test key, so a removed hook cannot be told from
    /// one that only missed an edge: put its view right and nothing else.</summary>
    Unproven,

    /// <summary>Windows says the key is up while the hook keeps receiving
    /// the key itself (its autorepeats): it is held, and something after
    /// this hook (another program's keyboard hook that swallows it) keeps it
    /// from Windows' key state. Not a missed release: nothing is done, and
    /// this is said once per hold, for the log.</summary>
    HeldBack,
}

/// <param name="Finding">What to do.</param>
/// <param name="KeyDown">Where Windows says the key is now, which is where
/// <see cref="IHookWatchTarget.Resync"/> puts the hook's view.</param>
/// <param name="Probe">With <see cref="HookFinding.Suspected"/>: send the
/// test key. Only with an ordinary window in front: Windows drops it with a
/// window running as administrator in front, and with none.</param>
/// <param name="Reveal">With <see cref="HookFinding.Lost"/>: bring the window
/// forward. Once per loss, the Mac's <c>DictationWindowAttention</c> rule:
/// edges, not levels.</param>
/// <param name="Healed">A reinstalled hook received its first key, so the
/// icon's state changed.</param>
/// <param name="Silent">How long the disagreement lasted; with
/// <see cref="HookFinding.Lost"/>, how long ago the test key went out.</param>
/// <param name="MissedPress">With <see cref="HookFinding.Lost"/>: the edge
/// the hook was found missing was a press (true) or a release.</param>
public readonly record struct HookVerdict(
    HookFinding Finding,
    bool KeyDown = false,
    bool Probe = false,
    bool Reveal = false,
    bool Healed = false,
    TimeSpan Silent = default,
    bool MissedPress = false);

/// <summary>
/// Decides, from readings of the hold key on both sides, whether the
/// hold-key hook is gone. One per armed hook; the controller samples it from
/// its poll whenever <see cref="Due"/> says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>The evidence.</b> When Windows' key state and the hook's view of the
/// hold key begin to disagree with an ordinary window in front, the hook is
/// sent a test key. One that has not reached it <see cref="ConfirmAfter"/>
/// later, while it received no other keyboard event either, means Windows
/// removed it: a removed hook receives nothing, and a live one counts the test
/// key once its thread pumps. That answer stands even when the two sides agree
/// again by then, so a short press (a latch's stop tap) still shows a dead
/// hook. A hook that receives anything is alive and only missed one edge. A
/// test key Windows refused proves nothing, and nor does a window that is not
/// an ordinary one when the answer is due: a window running as administrator in
/// front explains the silence by design and is never called a loss, and with
/// no window in front (a secure desktop, where Windows' key state reads up and
/// no hook hears anything) or one whose elevation cannot be told, a
/// disagreement waits for an ordinary window and starts again then, test key
/// included: a release made on the Ctrl+Alt+Del screen is a missed edge, not a
/// lost hook. A disagreement that began under an administrator window gets its
/// test key once an ordinary one is in front.
/// </para>
/// <para>
/// <b>A release the hook missed.</b> A live hook that says the key is down
/// while Windows says it is up is replayed a release only once the hold key
/// itself has been quiet at the hook for <see cref="ReleaseQuietAfter"/>, longer
/// than the longest keyboard repeat delay Windows offers. A key still held
/// sends autorepeats, so a hook that keeps receiving them while Windows' state
/// reads up is behind another program's hook that swallows the key
/// (<see cref="HookFinding.HeldBack"/>), and the hold carries on.
/// </para>
/// <para>
/// <b>The cadence.</b> One look every <see cref="SampleEvery"/>, and every
/// <see cref="RecheckEvery"/> while the two sides disagree or a test key is
/// out. A press shorter than <see cref="SampleEvery"/> can fall between two
/// looks and go unnoticed; one that is noticed is decided by its test key. The
/// lead's suggestion was every one to two seconds; half a second costs one
/// <c>GetAsyncKeyState</c> call and catches the shorter holds people dictate in.
/// </para>
/// <para>
/// <b>Reporting, the Mac's edge rule.</b> The first loss brings the window
/// forward (<see cref="HookVerdict.Reveal"/>); a hook found gone again before
/// the new one received anything does not, so a user who closes the window
/// is not asked twice for one fault. Once the new hook receives a key the
/// rule re-arms, and a later loss asks again, as the Mac's window comes back
/// "after a re-grant and a second loss".
/// </para>
/// </remarks>
public sealed class DictationHookWatch
{
    /// <summary>How often the two sides are compared while they agree.</summary>
    public static readonly TimeSpan SampleEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>How often they are compared while they disagree.</summary>
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a disagreement must last, and a test key go
    /// unanswered, to be acted on: longer than a key edge takes to reach a
    /// hook whose thread is busy for a moment, as at a press, when it opens the
    /// microphone.</summary>
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the hold key must have sent a live hook nothing
    /// before a release the hook missed is replayed: past Windows' longest
    /// keyboard repeat delay (about 1 s), so a key still held has sent an
    /// autorepeat by then.</summary>
    public static readonly TimeSpan ReleaseQuietAfter = TimeSpan.FromMilliseconds(1_200);

    /// <summary>One disagreement between the two sides, while it lasts.</summary>
    private sealed class Disagreement(TimeSpan since, HookReading reading)
    {
        public TimeSpan Since { get; } = since;

        /// <summary>Where Windows said the key was: a flip starts a new one.</summary>
        public bool KeyDown { get; } = reading.KeyDown;

        /// <summary>The hook's event count when it began.</summary>
        public long Events { get; } = reading.HookEvents;

        /// <summary>A test key was sent for it, or asked for and refused.</summary>
        public bool Tested { get; set; }

        /// <summary>Windows refused its test key.</summary>
        public bool TestKeyRefused { get; set; }

        /// <summary>A test key reached the hook while it lasted.</summary>
        public bool Answered { get; set; }

        /// <summary>The hold key's own event count, last time it moved.</summary>
        public long TriggerEvents { get; set; } = reading.TriggerEvents;

        /// <summary>Since when the hold key has sent the hook nothing.</summary>
        public TimeSpan TriggerQuietSince { get; set; } = since;

        /// <summary>Its autorepeats were seen with Windows saying up, and said.</summary>
        public bool HeldBack { get; set; }
    }

    /// <summary>A test key sent: when, the hook's event count just before,
    /// and which edge the hook had missed.</summary>
    private readonly record struct TestKey(TimeSpan SentAt, long Events, bool MissedPress);

    private HookHealth _health = HookHealth.Healthy;

    /// <summary>Whether the next loss brings the window forward: true until
    /// a loss, and again once a reinstalled hook received a key.</summary>
    private bool _revealArmed = true;

    private long _eventsAtRepair;
    private TimeSpan? _nextSample;
    private Disagreement? _disagreement;
    private TestKey? _testKey;

    /// <summary>Whether it is time for another reading.</summary>
    public bool Due(TimeSpan now) => _nextSample is not { } next || now >= next;

    /// <summary>Whether a disagreement is being watched or a test key is
    /// out: the window in front is then looked at on every poll, so the
    /// reading that decides never acts on a window that has gone since
    /// (<see cref="AdminWindowWatch"/>).</summary>
    public bool Suspecting => _disagreement is not null || _testKey is not null;

    /// <summary>The hook's own state, for the notification-area icon
    /// (<see cref="DictationTrayGlyph"/>, which ranks it against a blocked
    /// microphone and a window running as administrator in front).</summary>
    public HookHealth Health => _health;

    /// <summary>Fold one reading in and say what to do.</summary>
    /// <param name="now">The controller's clock.</param>
    /// <param name="reading">Both sides of the hold key, now.</param>
    /// <param name="window">What is in front (<see cref="AdminWindowWatch"/>).</param>
    public HookVerdict Observe(TimeSpan now, HookReading reading, WindowInFront window)
    {
        var healed = false;
        if (_health == HookHealth.Reconnected && reading.HookEvents != _eventsAtRepair)
        {
            _health = HookHealth.Healthy;
            _revealArmed = true;
            healed = true;
        }

        // A test key out for long enough is answered first, whether or not
        // the two sides still disagree: a short press is over before then,
        // and a live hook counts the test key either way.
        if (_testKey is { } key && now - key.SentAt >= ConfirmAfter)
        {
            _testKey = null;
            if (reading.HookEvents != key.Events)
            {
                if (_disagreement is { } answered)
                {
                    answered.Answered = true;
                }
            }
            else if (window == WindowInFront.Ordinary)
            {
                Settle(now);
                var reveal = _revealArmed;
                _revealArmed = false;
                return new HookVerdict(
                    HookFinding.Lost, reading.KeyDown, Reveal: reveal, Healed: healed, Silent: now - key.SentAt,
                    MissedPress: key.MissedPress);
            }

            // Otherwise another kind of window came to the front meanwhile,
            // which may have dropped the key: its silence proves nothing. A
            // disagreement it was sent for is 300 ms old by now, so the
            // reading below settles it (an administrator window) or drops it
            // (none, or one that cannot be told).
        }

        if (!reading.Disagree)
        {
            _disagreement = null;
            _nextSample = now + (_testKey is null ? SampleEvery : RecheckEvery);
            return new HookVerdict(HookFinding.None, reading.KeyDown, Healed: healed);
        }

        _nextSample = now + RecheckEvery;
        if (window is WindowInFront.None or WindowInFront.Unknown)
        {
            // Nothing can be told; start again, test key and all, once an
            // ordinary window is in front.
            _disagreement = null;
            _testKey = null;
            return new HookVerdict(HookFinding.None, reading.KeyDown, Healed: healed);
        }

        var started = false;
        if (_disagreement is not { } disagreement || disagreement.KeyDown != reading.KeyDown)
        {
            disagreement = new Disagreement(now, reading);
            _disagreement = disagreement;
            started = true;
        }

        // One test key per disagreement, once an ordinary window is in front,
        // unless one already out answers for it or already has.
        if (window == WindowInFront.Ordinary && !disagreement.Tested && !disagreement.Answered && _testKey is null)
        {
            disagreement.Tested = true;
            _testKey = new TestKey(now, reading.HookEvents, MissedPress: reading.KeyDown);
            return new HookVerdict(HookFinding.Suspected, reading.KeyDown, Probe: true, Healed: healed);
        }

        if (started)
        {
            return new HookVerdict(HookFinding.Suspected, reading.KeyDown, Healed: healed);
        }

        var silent = now - disagreement.Since;
        if (silent < ConfirmAfter)
        {
            return new HookVerdict(HookFinding.None, reading.KeyDown, Healed: healed);
        }

        if (window == WindowInFront.Administrator)
        {
            Settle(now);
            return new HookVerdict(HookFinding.AdminWindow, reading.KeyDown, Healed: healed, Silent: silent);
        }

        var alive = disagreement.Answered || reading.HookEvents != disagreement.Events;
        if (!alive && !disagreement.TestKeyRefused)
        {
            // Its test key's answer is not in yet.
            return new HookVerdict(HookFinding.None, reading.KeyDown, Healed: healed);
        }

        if (!reading.KeyDown)
        {
            if (reading.TriggerEvents != disagreement.TriggerEvents)
            {
                // The key itself keeps arriving: held, and kept from Windows'
                // key state by something after this hook.
                disagreement.TriggerEvents = reading.TriggerEvents;
                disagreement.TriggerQuietSince = now;
                if (!disagreement.HeldBack)
                {
                    disagreement.HeldBack = true;
                    return new HookVerdict(HookFinding.HeldBack, reading.KeyDown, Healed: healed, Silent: silent);
                }

                return new HookVerdict(HookFinding.None, reading.KeyDown, Healed: healed);
            }

            if (now - disagreement.TriggerQuietSince < ReleaseQuietAfter)
            {
                return new HookVerdict(HookFinding.None, reading.KeyDown, Healed: healed);
            }
        }

        Settle(now);
        return new HookVerdict(
            alive ? HookFinding.EdgeMissed : HookFinding.Unproven, reading.KeyDown, Healed: healed, Silent: silent);
    }

    /// <summary>
    /// The controller could not send the test key <see cref="Observe"/> asked
    /// for: Windows refused it (another program blocking input, a window above
    /// this one), or the call failed. Its silence then proves nothing, so the
    /// disagreement ends in <see cref="HookFinding.Unproven"/>, never in a
    /// loss, and asks for no other.
    /// </summary>
    public void TestKeyRefused()
    {
        _testKey = null;
        if (_disagreement is { } refused)
        {
            refused.TestKeyRefused = true;
        }
    }

    /// <summary>The controller acted on <see cref="HookFinding.Lost"/>.</summary>
    /// <param name="reinstalled">Whether a new hook is in place.</param>
    /// <param name="hookEvents">The event count right after, so the new
    /// hook's first key is noticed.</param>
    public void Repaired(bool reinstalled, long hookEvents)
    {
        _health = reinstalled ? HookHealth.Reconnected : HookHealth.Unheard;
        _eventsAtRepair = hookEvents;
    }

    /// <summary>A decision was made, which puts the two sides back in
    /// agreement: back to the resting cadence.</summary>
    private void Settle(TimeSpan now)
    {
        _disagreement = null;
        _testKey = null;
        _nextSample = now + SampleEvery;
    }
}

/// <summary>
/// What kind of window is in front, and so whether one running as
/// administrator is, asked from the controller's poll every
/// <see cref="CheckEvery"/> (and on every poll while the hook watch is
/// <see cref="DictationHookWatch.Suspecting"/>), its elevation cached for the
/// process in front: a new process in front is asked once, and the same
/// one again only after another took its place, so a process id reused by a
/// later process is never answered from the cache.
/// </summary>
public sealed class AdminWindowWatch(IForegroundElevation probe)
{
    /// <summary>How often the window in front is looked at.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(500);

    private TimeSpan? _next;
    private int _process = -1;
    private WindowInFront _processKind;

    /// <summary>What was in front at the last look.</summary>
    public WindowInFront Kind { get; private set; } = WindowInFront.Ordinary;

    /// <summary>Whether a window running as administrator was in front at the
    /// last look.</summary>
    public bool InFront => Kind == WindowInFront.Administrator;

    /// <summary>Whether it is time for another look.</summary>
    public bool Due(TimeSpan now) => _next is not { } next || now >= next;

    /// <summary>Look at the window in front.</summary>
    /// <returns>Whether <see cref="InFront"/> changed: the hint comes or
    /// goes.</returns>
    /// <remarks>A probe that throws leaves the last answer standing and is
    /// asked again at the next look; the caller logs it.</remarks>
    public bool Check(TimeSpan now)
    {
        _next = now + CheckEvery;
        var was = InFront;
        Kind = Read();
        return InFront != was;
    }

    private WindowInFront Read()
    {
        if (probe.ThisProcessElevated)
        {
            // No ordinary window is above Dictation then. With none in front,
            // the hook hears nothing either way.
            return probe.ForegroundProcessId() > 0 ? WindowInFront.Ordinary : WindowInFront.None;
        }

        var process = probe.ForegroundProcessId();
        if (process <= 0)
        {
            // No window in front (a secure desktop, a switch under way):
            // nothing to explain. The cached answer goes with it: the process
            // in front before may have exited meanwhile and its id been reused.
            _process = -1;
            return WindowInFront.None;
        }

        if (process != _process)
        {
            // Unknown is not ordinary: a process that cannot be asked may well
            // be above this one, where the hook and the test key are both cut
            // off, and calling it ordinary would make every press there a
            // lost hook.
            _processKind = probe.IsElevated(process) switch
            {
                true => WindowInFront.Administrator,
                false => WindowInFront.Ordinary,
                null => WindowInFront.Unknown,
            };
            _process = process;
        }

        return _processKind;
    }
}
