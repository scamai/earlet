using Dictation.Speech;

namespace Dictation.Core;

/// <summary>"Silence stops hands-free", the user's choice in the window: the
/// hands-free silence stop after 30 s (the default) or 60 s of silence, or
/// never.</summary>
public enum DictationSilenceStop
{
    After30Seconds,
    After60Seconds,
    Off,
}

/// <summary>Why the app ended a listening hold itself, with no key edge.</summary>
public enum DictationHoldStop
{
    /// <summary>The recording limit (<see cref="DictationHoldPolicy.Ceiling"/>),
    /// or the memory cap behind it. Transcribed and inserted.</summary>
    Ceiling,

    /// <summary>Hands-free: speech, then the chosen silence. Transcribed and
    /// inserted.</summary>
    Silence,

    /// <summary>Hands-free: nothing heard since the latch for
    /// <see cref="DictationHoldPolicy.NothingHeardStop"/>. Ended as a release,
    /// as every stop is: the session's quiet-room check keeps a silent room
    /// from the recogniser, so nothing goes in, and speech the tracker missed
    /// (soft, in a noisy room) is transcribed and inserted, not lost.</summary>
    NothingHeard,

    /// <summary>Held: no speech for <see cref="DictationHoldPolicy.StuckKeyStop"/>
    /// while the key stayed down, most likely a key that is stuck or
    /// weighed down. Transcribed and inserted.</summary>
    StuckKey,
}

/// <summary>A stop the app made, and the length it names: the limit, the
/// silence or the wait.</summary>
public sealed record DictationHoldEnding(DictationHoldStop Stop, TimeSpan After);

/// <summary>A listening hold at one tick, as the policy judges it.</summary>
/// <param name="Elapsed">Time since the microphone came up.</param>
/// <param name="Latched">The hold is latched hands-free (the key is up).</param>
/// <param name="HeardSpeech">The silence tracker heard speech in this hold.</param>
/// <param name="SilentFor">Time since the last speech, or since the
/// microphone came up when none was heard.</param>
public readonly record struct DictationHoldMoment(TimeSpan Elapsed, bool Latched, bool HeardSpeech, TimeSpan SilentFor);

/// <summary>A silence stop is near, and the indicator says so: hands-free
/// after speech, or held, before the stuck-key stop.</summary>
/// <param name="SilentFor">The silence so far.</param>
/// <param name="StopsIn">Until the silence stop ends the hold.</param>
public readonly record struct DictationSilenceWarning(TimeSpan SilentFor, TimeSpan StopsIn);

/// <summary>The policy's answer for one <see cref="DictationHoldMoment"/>.</summary>
/// <param name="Ending">End the hold now, and why; null to let it run.</param>
/// <param name="Warning">The hands-free silence stop (after speech) or the
/// held stuck-key stop is within <see cref="DictationHoldPolicy.SilenceWarning"/>.
/// Null otherwise.</param>
/// <param name="CountingDown">The hold is in the last
/// <see cref="DictationHoldPolicy.Countdown"/> before the limit.</param>
public readonly record struct DictationHoldVerdict(DictationHoldEnding? Ending, DictationSilenceWarning? Warning, bool CountingDown);

/// <summary>
/// Every rule about how long a Windows dictation may listen, in one place:
/// the recording limit and the countdown before it, the memory cap behind
/// the limit, and the silence stops. It replaces the two-minute
/// <c>MicrophoneTapOptions.Dictation.MaximumHold</c>, the tap's
/// <c>MaximumLatched</c>, the controller's <c>HandsFreeTap</c> and the
/// indicator's <c>ListeningCountdownSeconds</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rule zero: a stop never loses words.</b> Every stop ends the hold as a
/// release does: what was captured is transcribed and inserted. The
/// never-spoke stop (<see cref="DictationHoldStop.NothingHeard"/>) too: the
/// tracker can miss soft speech in a noisy room, so it is the session's
/// quiet-room check, as at any release, that keeps a silent room from the
/// recogniser. The cost is the release's own: a noisy room nobody spoke into
/// reaches the recogniser, which may type a stock phrase for it.
/// </para>
/// <para>
/// <b>Five minutes on Windows, for now.</b> The wait after a release grows
/// with the length, because the worker decodes the whole take only then
/// (measured on a Linux server CPU, small model: 56 s at 5 min, 112 s at 10 min). Five
/// minutes is the stage-1 limit until the worker decodes during the hold;
/// then it moves to twenty, long enough for a dictated draft. The Mac stays at
/// two minutes until its own stage 1.
/// </para>
/// <para>
/// <b>The memory cap is its own number.</b> The tap stops keeping audio at
/// <see cref="MemoryCap"/>, a minute past the limit, and raises
/// <see cref="PushToTalkMicrophoneTap.BufferLimitReached"/>, which ends the
/// hold as the limit does. It is reached only when the limit was not
/// enforced in time (a UI thread stalled past it). Changing
/// <see cref="Ceiling"/> does not move it; <see cref="Checked"/> refuses a
/// limit at or past it, so a longer limit has to raise the cap on purpose.
/// </para>
/// </remarks>
public sealed record DictationHoldPolicy
{
    /// <summary>The Windows product's policy.</summary>
    public static DictationHoldPolicy Windows { get; } = new();

    /// <summary>The longest recording, held or hands-free: 5:00. At it the
    /// hold is transcribed and inserted.</summary>
    public TimeSpan Ceiling { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long before <see cref="Ceiling"/> the indicator counts
    /// down, and the alert sound plays once: 60 s, enough to finish a sentence.</summary>
    public TimeSpan Countdown { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The tap's buffer bound: 6:00, a minute past the limit.</summary>
    public TimeSpan MemoryCap { get; init; } = TimeSpan.FromMinutes(6);

    /// <summary>Hands-free with nothing heard yet: stop, unheard, after 15 s.</summary>
    public TimeSpan NothingHeardStop { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The indicator warns this long before a silence stop, 10 s:
    /// hands-free after speech (at 20 s of a 30 s stop), and held before the
    /// stuck-key stop (at 50 s). Silently: the user is probably thinking.</summary>
    public TimeSpan SilenceWarning { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Held: no speech for a minute with the key down ends the hold
    /// and inserts it, after the same warning as the hands-free stop. A held
    /// key otherwise never stops on silence: holding it is the user saying
    /// they mean to talk. The warning is there because what is said after
    /// this stop, with the key still down, is not recorded, and a voice the
    /// tracker cannot hear (soft, in a noisy room) would be cut off unwarned.</summary>
    public TimeSpan StuckKeyStop { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether silence ends holds at all. Always true in the product; a test
    /// about something else (the limit, a jump of the fake clock) turns it
    /// off with <see cref="WithoutSilenceStops"/>, so that its long wait is
    /// not read as a silent room.
    /// </summary>
    public bool StopsOnSilence { get; init; } = true;

    /// <summary>
    /// The microphone tap's options under this policy: no minimum hold, and
    /// the memory cap.
    /// </summary>
    /// <remarks>
    /// No floor, for the hands-free gesture. The hook keeps a short press's
    /// hold open until press + the double-tap window, so the pair's first tap
    /// flows into the latch with no gap; a lone quick tap ends there and is
    /// transcribed, as on the Mac, which has no floor either. The floor
    /// measures the captured AUDIO, which can start hundreds of milliseconds
    /// after the press on a slow device, so keeping it would discard some of
    /// those taps as "too short" after the user waited them out.
    /// </remarks>
    public MicrophoneTapOptions TapOptions => new(TimeSpan.Zero, MemoryCap);

    /// <summary>The same policy with no silence stop of any kind.</summary>
    public DictationHoldPolicy WithoutSilenceStops() => this with { StopsOnSilence = false };

    /// <summary>The hands-free silence that ends a hold under
    /// <paramref name="setting"/>; null when it is off.</summary>
    public static TimeSpan? SilenceStop(DictationSilenceStop setting) => setting switch
    {
        DictationSilenceStop.After60Seconds => TimeSpan.FromSeconds(60),
        DictationSilenceStop.Off => null,
        _ => TimeSpan.FromSeconds(30),
    };

    /// <summary>This policy, when its numbers can work together.</summary>
    /// <exception cref="ArgumentException">A limit at or past the memory
    /// cap (the tap would drop words before the limit stopped the hold), a
    /// countdown that does not fit before the limit, a silence warning that
    /// does not fit before the shortest silence stop it warns of (hands-free
    /// or stuck-key), or a stop that is not positive.</exception>
    public DictationHoldPolicy Checked()
    {
        if (Ceiling <= TimeSpan.Zero || NothingHeardStop <= TimeSpan.Zero || StuckKeyStop <= TimeSpan.Zero)
        {
            throw new ArgumentException("The recording limit and the silence stops must be positive.");
        }

        if (MemoryCap <= Ceiling)
        {
            throw new ArgumentException(
                $"The memory cap ({MemoryCap}) must lie past the recording limit ({Ceiling}), or the tap drops words before the limit stops the hold.");
        }

        if (Countdown < TimeSpan.Zero || Countdown >= Ceiling)
        {
            throw new ArgumentException($"The countdown ({Countdown}) must fit before the limit ({Ceiling}).");
        }

        if (SilenceWarning < TimeSpan.Zero || SilenceWarning >= SilenceStop(DictationSilenceStop.After30Seconds)
            || SilenceWarning >= StuckKeyStop)
        {
            throw new ArgumentException($"The silence warning ({SilenceWarning}) must fit before the silence stop.");
        }

        return this;
    }

    /// <summary>
    /// What a listening hold should do at <paramref name="moment"/>.
    /// </summary>
    /// <remarks>
    /// The limit first, so a hold past it ends there whatever the silence
    /// says. Then, hands-free, the user's silence stop: with nothing heard
    /// yet, <see cref="NothingHeardStop"/>; after speech, the chosen silence,
    /// with a warning over its last <see cref="SilenceWarning"/>; off,
    /// neither. Held, only <see cref="StuckKeyStop"/>, with the same warning.
    /// </remarks>
    /// <param name="moment">The hold now.</param>
    /// <param name="setting">The user's hands-free silence stop.</param>
    public DictationHoldVerdict Judge(DictationHoldMoment moment, DictationSilenceStop setting)
    {
        if (moment.Elapsed >= Ceiling)
        {
            return new(new(DictationHoldStop.Ceiling, Ceiling), null, true);
        }

        var counting = moment.Elapsed >= Ceiling - Countdown;
        if (!StopsOnSilence)
        {
            return new(null, null, counting);
        }

        if (!moment.Latched)
        {
            return moment.SilentFor >= StuckKeyStop
                ? new(new(DictationHoldStop.StuckKey, StuckKeyStop), null, counting)
                : new(null, Warn(moment.SilentFor, StuckKeyStop), counting);
        }

        if (SilenceStop(setting) is not { } stop)
        {
            return new(null, null, counting);
        }

        if (!moment.HeardSpeech)
        {
            return moment.SilentFor >= NothingHeardStop
                ? new(new(DictationHoldStop.NothingHeard, NothingHeardStop), null, counting)
                : new(null, null, counting);
        }

        if (moment.SilentFor >= stop)
        {
            return new(new(DictationHoldStop.Silence, stop), null, counting);
        }

        return new(null, Warn(moment.SilentFor, stop), counting);
    }

    /// <summary>The warning over the last <see cref="SilenceWarning"/> before
    /// <paramref name="stop"/>, or null before it.</summary>
    private DictationSilenceWarning? Warn(TimeSpan silentFor, TimeSpan stop) =>
        stop - silentFor is var left && left <= SilenceWarning ? new DictationSilenceWarning(silentFor, left) : null;
}

/// <summary>One choice of the window's "Silence stops hands-free" picker.</summary>
/// <param name="Value">The setting.</param>
/// <param name="Name">What the picker shows.</param>
public sealed record DictationSilenceStopChoice(DictationSilenceStop Value, string Name)
{
    /// <summary>Every choice, in the picker's order: 30 s, 60 s, never.</summary>
    public static IReadOnlyList<DictationSilenceStopChoice> All { get; } =
        [.. Enum.GetValues<DictationSilenceStop>().Select(v => new DictationSilenceStopChoice(v, DictationCopy.SilenceStopChoice(v)))];

    /// <summary>The choice for <paramref name="value"/>; the default one for
    /// a value this build does not know.</summary>
    public static DictationSilenceStopChoice For(DictationSilenceStop value) =>
        All.FirstOrDefault(c => c.Value == value) ?? All[0];
}
