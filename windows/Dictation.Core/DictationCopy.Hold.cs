using Dictation.Speech;

namespace Dictation.Core;

// Part of DictationCopy (see DictationCopy.cs): what the window and the
// indicator say about how long a hold may listen (DictationHoldPolicy) — the
// listening pill's clock, the countdown before the limit and the silence
// warning; the one width every busy pill shares; the endings and status lines
// of the stops the app makes itself; and the hands-free silence setting.
public static partial class DictationCopy
{
    // ─── The listening pill ─────────────────────────────────────────────

    /// <summary>The held countdown's stop hint.</summary>
    private const string HeldStop = "Release to insert";

    /// <summary>The hands-free stop hint: the Mac's, with Windows' Enter.</summary>
    public static string HudLatchedStop(string keyName) => $"Press {keyName}, Space or Enter to stop";

    /// <summary>
    /// The listening pill, the Mac's <c>DictationHUDContent.hint(for:
    /// .recording(latched:), elapsed:, limit:)</c> under "Listening". Held:
    /// <see cref="HudHeld"/>. Hands-free: "Listening — m:ss · Press Right
    /// Ctrl, Space or Enter to stop". In the policy's last
    /// <see cref="DictationHoldPolicy.Countdown"/> before the limit, held or
    /// hands-free: "Listening — Stops in 1:00 · Release to insert" or "… ·
    /// Press Right Ctrl, Space or Enter to stop". The clock is floored and
    /// the time left rounded up, neither below zero.
    /// </summary>
    /// <remarks>
    /// A <paramref name="silence"/> warning, hands-free or held, takes the
    /// line when its stop comes before the limit's: "No speech for 20 s —
    /// stopping in 10 s. Keep talking to continue" (<see cref="HudSilenceWarning"/>).
    /// </remarks>
    /// <param name="keyName">The hold key's name.</param>
    /// <param name="latched">The hold is latched hands-free.</param>
    /// <param name="elapsed">Time since the microphone came up.</param>
    /// <param name="policy">The limit and the countdown before it.</param>
    /// <param name="silence">The silence stop's warning, when one is due.</param>
    public static string HudListening(
        string keyName, bool latched, TimeSpan elapsed, DictationHoldPolicy policy, DictationSilenceWarning? silence = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var remaining = policy.Ceiling - elapsed;
        if (silence is { } warning && warning.StopsIn < remaining)
        {
            return HudSilenceWarning(warning);
        }

        var stop = latched ? HudLatchedStop(keyName) : HeldStop;
        if (remaining <= policy.Countdown)
        {
            return Listening("Stops in " + HudClock(Math.Ceiling(remaining.TotalSeconds)), stop);
        }

        return latched ? Listening(HudClock(elapsed.TotalSeconds), stop) : HudHeld;
    }

    /// <summary>
    /// Close to a silence stop, hands-free after speech or held before the
    /// stuck-key stop: "No speech for 20 s — stopping in 10 s. Keep talking
    /// to continue" (held: "No speech for 50 s — stopping in 10 s …"), the
    /// silence floored and the time left rounded up, redrawn as each second
    /// passes.
    /// Shown and never spoken, as every listening pill: the microphone is
    /// open. No sound either: the user is probably thinking.
    /// </summary>
    public static string HudSilenceWarning(DictationSilenceWarning warning) =>
        SilenceWarning(
            Math.Max(0, (long)Math.Floor(warning.SilentFor.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Math.Max(0, (long)Math.Ceiling(warning.StopsIn.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string SilenceWarning(string silent, string left) =>
        $"No speech for {silent} s — stopping in {left} s. Keep talking to continue";

    /// <summary>
    /// Every line a busy pill can show for this key under
    /// <paramref name="policy"/>: the pill is sized for the widest of them
    /// and keeps that one width from the press to the ending, as the Mac's
    /// capsule does (<c>DictationHUDMetrics.textColumnWidth</c>). The held
    /// and hands-free hints; the hands-free clock at "00:00", a fixed
    /// reference that holds any clock up to 99:59, so the width does not
    /// depend on the limit; the countdown at its start; the Transcribing
    /// line; the live preview's caption budget (seven words, sixteen CJK
    /// characters), so a caption fits without a resize; and Windows' own
    /// Typing. The silence warning is not listed: each of its lines is
    /// narrower than the countdown's for every hold key, which the width
    /// tests check at every half second of both silence stops.
    /// </summary>
    /// <remarks>
    /// The window measures text with <c>FormattedText</c>, which does not
    /// apply the second line's tabular digits, so each number-bearing line is
    /// listed once with every digit as 0, once as 1, and so on to 9:
    /// whichever digit the font draws widest, no line of a hold is wider than
    /// the widest of these. The key's own name is left as it is (F13 keeps
    /// its digits).
    /// </remarks>
    /// <param name="keyName">The hold key's name.</param>
    /// <param name="policy">The countdown the lines count.</param>
    public static IReadOnlyList<string> HudBusyLines(string keyName, DictationHoldPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var countdownStart = HudClock(policy.Countdown.TotalSeconds);
        var lines = new List<string>
        {
            HudHeld,
            HudTranscribing,
            HudTyping,
            CaptionReferenceWords,
            CaptionReferenceCjk,
        };
        for (var digit = '0'; digit <= '9'; digit++)
        {
            var clock = EveryDigitAs("00:00", digit);
            var left = "Stops in " + EveryDigitAs(countdownStart, digit);
            lines.Add(Listening(clock, HudLatchedStop(keyName)));
            lines.Add(Listening(left, HudLatchedStop(keyName)));
            lines.Add(Listening(left, HeldStop));
        }

        return lines.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string Listening(string time, string stop) => $"{ListeningTitle} — {time} · {stop}";

    private static string EveryDigitAs(string clock, char digit) =>
        new(clock.Select(c => char.IsAsciiDigit(c) ? digit : c).ToArray());

    /// <summary>Whole seconds as m:ss, floored ("1:05"), never negative.</summary>
    public static string HudClock(double seconds)
    {
        var whole = Math.Max(0, (long)Math.Floor(seconds));
        return $"{whole / 60}:{whole % 60:00}";
    }

    // ─── The stops the app makes itself: the indicator ──────────────────

    /// <summary>
    /// The limit ended the hold and its words went in: the Mac's "Stopped at
    /// 2:00" ending, with the limit as m:ss and its "what to do" joined into
    /// one line, as every Windows line is: "Stopped at 5:00 — your words went
    /// in, hold Right Ctrl again to keep going".
    /// </summary>
    /// <param name="keyName">The hold key's name.</param>
    /// <param name="limit">The longest recording.</param>
    public static string HudStoppedAtLimit(string keyName, TimeSpan limit) =>
        $"Stopped at {HudClock(limit.TotalSeconds)} — your words went in, hold {keyName} again to keep going";

    /// <summary>Hands-free, the silence stop ended the hold and its words
    /// went in. Double-tap, not hold: that is how hands-free starts again.</summary>
    /// <param name="keyName">The hold key's name.</param>
    /// <param name="silence">The silence that ended it.</param>
    public static string HudStoppedAfterSilence(string keyName, TimeSpan silence) =>
        $"Stopped after {Seconds(silence)} s of silence — your words went in, double-tap {keyName} to keep going";

    /// <summary>Held, the stuck-key stop ended the hold and its words went
    /// in: the key is still down, so it has to come up first.</summary>
    /// <param name="keyName">The hold key's name.</param>
    /// <param name="silence">The silence that ended it.</param>
    public static string HudStoppedStuckKey(string keyName, TimeSpan silence) =>
        $"Stopped — no speech for {Duration(silence)} with {keyName} held. Your words went in";

    /// <summary>Hands-free, nothing was heard since the latch, the hold ended
    /// as a release, and the quiet-room check found nothing to send, so
    /// nothing went in.</summary>
    /// <param name="keyName">The hold key's name.</param>
    /// <param name="wait">How long it listened.</param>
    public static string HudNothingHeardStopped(string keyName, TimeSpan wait) =>
        $"Nothing heard in {Seconds(wait)} s — stopped, double-tap {keyName} to try again";

    // ─── The stops the app makes itself: the window ─────────────────────

    /// <summary>
    /// The status line while a hold the app ended is transcribed: the old
    /// "Two minutes is the longest recording, so it stopped there.
    /// Transcribing…", for each stop. Nothing heard reads as the silence
    /// stop: the recogniser is asked only when the room was not silent.
    /// </summary>
    public static string StoppedTranscribing(DictationHoldEnding ending, HotkeyBinding key)
    {
        ArgumentNullException.ThrowIfNull(ending);
        ArgumentNullException.ThrowIfNull(key);
        return ending.Stop switch
        {
            DictationHoldStop.Ceiling =>
                $"{Capitalised(Length(ending.After))} is the longest recording, so it stopped there. Transcribing…",
            DictationHoldStop.Silence or DictationHoldStop.NothingHeard =>
                $"No speech for {Length(ending.After)}, so hands-free dictation stopped. Transcribing…",
            DictationHoldStop.StuckKey =>
                $"No speech for {Duration(ending.After)} with {key.KeyName} held, so it stopped. Transcribing…",
            _ => Transcribing,
        };
    }

    /// <summary>The status line once the words of a hold the app ended went
    /// in, saying why it stopped and how to go on. After nothing heard, the
    /// words are ones the silence tracker missed: it stopped on silence.</summary>
    public static string InsertedAfterStop(DictationHoldEnding ending, HotkeyBinding key)
    {
        ArgumentNullException.ThrowIfNull(ending);
        ArgumentNullException.ThrowIfNull(key);
        return ending.Stop switch
        {
            DictationHoldStop.Silence or DictationHoldStop.NothingHeard =>
                $"Text inserted. Hands-free dictation stopped after {Length(ending.After)} without speech; "
                + $"double-tap {key.KeyName} to keep going.",
            DictationHoldStop.StuckKey =>
                $"Text inserted. Recording stopped after {Duration(ending.After)} without speech; "
                + $"release {key.KeyName}, then hold it again to keep going.",
            _ => $"Text inserted. A recording stops at {Length(ending.After)}; hold {key.KeyName} again to continue.",
        };
    }

    /// <summary>Hands-free, nothing heard since the latch, and nothing to
    /// send: the hold ended and nothing went in.</summary>
    public static string NothingHeardStopped(TimeSpan wait, HotkeyBinding key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"Nothing was heard for {Length(wait)}, so hands-free dictation stopped. "
            + $"Nothing was inserted. Double-tap {key.KeyName} to try again.";
    }

    // ─── The hands-free silence setting ─────────────────────────────────

    /// <summary>A choice of the window's "Silence stops hands-free" picker.</summary>
    public static string SilenceStopChoice(DictationSilenceStop choice) => choice switch
    {
        DictationSilenceStop.After60Seconds => "After 60 seconds",
        DictationSilenceStop.Off => "Never",
        _ => "After 30 seconds",
    };

    public const string SilenceStopNotSaved =
        "The hands-free silence choice could not be saved. It applies until Earlet restarts. " + LogDetails;

    // ─── Lengths in words ───────────────────────────────────────────────

    private static readonly string[] Numbers =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    ];

    /// <summary>A length in a sentence: whole minutes from two to twenty in
    /// words ("five minutes"), anything else in seconds ("30 seconds").</summary>
    private static string Length(TimeSpan length)
    {
        var minutes = length.TotalMinutes;
        return minutes >= 2 && minutes <= 20 && minutes == Math.Floor(minutes)
            ? $"{Numbers[(int)minutes]} minutes"
            : $"{Seconds(length)} seconds";
    }

    /// <summary>A silence in a sentence: "a minute" for exactly one,
    /// otherwise <see cref="Length"/>.</summary>
    private static string Duration(TimeSpan length) => length == TimeSpan.FromMinutes(1) ? "a minute" : Length(length);

    private static string Seconds(TimeSpan length) =>
        ((long)Math.Round(length.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
