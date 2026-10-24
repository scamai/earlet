using Dictation.Services;
using Dictation.Speech;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The dictation indicator's rules, swept across both enums.
/// </summary>
/// <remarks>
/// Sweeps rather than examples, deliberately. The failure this HUD exists to
/// fix is a state that says nothing, so a state or outcome added later and not
/// handled must fail here rather than ship as a pill that never appears.
/// </remarks>
public sealed class DictationHudTests
{
    public static TheoryData<DictationState> AllStates()
    {
        var data = new TheoryData<DictationState>();
        foreach (var s in Enum.GetValues<DictationState>())
        {
            data.Add(s);
        }

        return data;
    }

    public static TheoryData<DictationOutcome> AllOutcomes()
    {
        var data = new TheoryData<DictationOutcome>();
        foreach (var o in Enum.GetValues<DictationOutcome>())
        {
            data.Add(o);
        }

        return data;
    }

    // ── The colour discipline, which is product-wide and not this feature's ──

    [Theory]
    [MemberData(nameof(AllStates))]
    public void NoStateEverPaintsAVerdictColour(DictationState state)
    {
        // RULES.md § 2: green is a genuine verdict and nothing else, red is a
        // confirmed deepfake and never decoration. Dictation renders neither —
        // it has no opinion about a face. A dictation success that reached for
        // green would be claiming something Dictation did not measure.
        var tone = DictationHudModel.For(state).Tone;

        Assert.NotEqual(HudTone.Genuine, tone);
        Assert.NotEqual(HudTone.Deepfake, tone);
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void NoOutcomeEverPaintsAVerdictColour(DictationOutcome outcome)
    {
        var tone = DictationHudModel.For(Result(outcome)).Tone;

        Assert.NotEqual(HudTone.Genuine, tone);
        Assert.NotEqual(HudTone.Deepfake, tone);
    }

    // ── Nothing finishes silently ────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void EveryOutcomeSaysSomething(DictationOutcome outcome)
    {
        var model = DictationHudModel.For(Result(outcome));

        Assert.True(model.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(model.Text));
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void NoOutcomeFallsThroughToItsMemberName(DictationOutcome outcome)
    {
        // EveryOutcomeSaysSomething above asserts the pill is not blank, and
        // the raw member name is not blank — so it passed for an outcome nobody
        // had written copy for, and the first the user would know is a pill
        // reading "SpokenCommand". The Recent pane has had this guard since it
        // was written (DictationSettingsViewModelTests
        // .EveryDictationOutcome_HasItsOwnSentence); the HUD, which is the more
        // visible of the two surfaces, did not.
        //
        // The default arm renders the member name deliberately, so an unhandled
        // outcome shows up as something odd rather than as a pill that never
        // came. That makes "the text equals the member name" the exact
        // signature of missing copy, and this is the assert that catches it.
        // Cancelled is the one member whose correct copy IS its own name. The
        // pill says "Cancelled" on purpose — the user is the one who cancelled
        // it, so the shortest true word is the whole message, and the Recent
        // pane spells it out ("Cancelled before anything was typed") where
        // there is room. Exempted by name rather than by loosening the rule,
        // so the next outcome without copy still fails here.
        if (outcome == DictationOutcome.Cancelled)
        {
            return;
        }

        var text = DictationHudModel.For(Result(outcome)).Text;

        Assert.NotEqual(outcome.ToString(), text);
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void EveryOutcomeGoesAwayByItself(DictationOutcome outcome)
    {
        // A finished dictation with no dwell is a pill that stays on top of
        // every window until the next hold — which on a feature nobody uses
        // twice in a row means forever.
        var dwell = DictationHudModel.For(Result(outcome)).Dwell;

        Assert.NotNull(dwell);
        Assert.True(dwell > TimeSpan.Zero);
    }

    // ── How long the last pill stays ─────────────────────────────────────
    //
    // The axis these assertions are drawn on is not success/failure, it is who
    // is carrying the message. The a dictation-history.jsonl put the HUD
    // on screen for 3.3-4.3 s after they stopped speaking (DecMs 769-1778,
    // InsMs ~500, plus a two-second dwell), and the last two of those seconds
    // began AFTER the words had appeared in their application. The pill was
    // asserting something they could already read.

    [Fact]
    public void TheThreeTiersAreOrderedByHowMuchThereIsToRead()
    {
        // Receipt: the outcome is legible somewhere other than the pill.
        // Notice: nothing was typed, and the reason is about the sentence.
        // Failure: nothing was typed, and the reason is about the machine —
        // often with somewhere to go, which is the slowest thing here to read.
        Assert.True(DictationHudModel.ReceiptDwell < DictationHudModel.NoticeDwell);
        Assert.True(DictationHudModel.NoticeDwell < DictationHudModel.FailureDwell);
    }

    [Fact]
    public void ADeliveredDictationGetsOutOfTheWay()
    {
        // The change this file exists to pin. Two seconds of "Typed into
        // Slack" is two seconds of a floating always-on-top pill telling the
        // user something the words in front of them already said.
        Assert.Equal(
            DictationHudModel.ReceiptDwell,
            DictationHudModel.For(Result(DictationOutcome.Inserted)).Dwell);
        Assert.True(DictationHudModel.ReceiptDwell < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void NothingIsGoneBeforeItCanBeSeen(DictationOutcome outcome)
    {
        // A dwell of zero, or near it, is the other failure mode: the final
        // pill is put up and taken down in the same frame, the state before it
        // ("Typing") is the last thing on screen, and a HUD that vanishes as
        // its last state lands reads as a crash rather than as a finish.
        var dwell = DictationHudModel.For(Result(outcome)).Dwell;

        Assert.NotNull(dwell);
        Assert.True(
            dwell >= PerceptibleFloor,
            $"{outcome} shows for {dwell!.Value.TotalMilliseconds:0} ms, which is a flicker");
    }

    [Fact]
    public void TheReceiptTierIsONLYForOutcomesTheUserCanSeeForThemselves()
    {
        // Pinned as a set rather than case by case, so an outcome added later
        // cannot join the fast tier by accident. Inserted: the words are in
        // the document. Cancelled: the user is the one who cancelled it.
        // Everything else has something to say that is said nowhere else.
        var fast = Enum.GetValues<DictationOutcome>()
            .Where(o => DictationHudModel.For(Result(o)).Dwell == DictationHudModel.ReceiptDwell)
            .ToHashSet();

        Assert.Equal(
            new HashSet<DictationOutcome>
            {
                DictationOutcome.Inserted,
                DictationOutcome.Cancelled,
            },
            fast);
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void ACopyCarryingOutcomeStaysUpLongEnoughToReadItsOwnCopy(DictationOutcome outcome)
    {
        // The point of the middle tier is that it is SHORTER, so the thing
        // worth asserting is that shortening it did not take it below its own
        // sentence. Held against the copy rather than against a number, so a
        // longer sentence written into the notice tier later fails here
        // instead of shipping as a pill nobody can finish reading.
        var model = DictationHudModel.For(Result(outcome));

        if (model.Dwell == DictationHudModel.ReceiptDwell)
        {
            // The receipt tier carries no reason to read — see above.
            return;
        }

        var needed = ReadingTimeFor(model.Text);

        Assert.True(
            model.Dwell >= needed,
            $"{outcome} says \"{model.Text}\" for {model.Dwell!.Value.TotalMilliseconds:0} ms "
                + $"and needs about {needed.TotalMilliseconds:0} ms to read");
    }

    [Fact]
    public void TheLongestFailureCopyIsStillTheLongestTier()
    {
        // Non-vacuity for the assertion above: it only means something if the
        // copy it measures actually varies, and if the outcomes with somewhere
        // to send the user are the ones getting the most time.
        var blocked = DictationHudModel.For(Result(DictationOutcome.MicrophoneBlocked));
        var tooShort = DictationHudModel.For(Result(DictationOutcome.HoldTooShort));

        Assert.True(ReadingTimeFor(blocked.Text) > ReadingTimeFor(tooShort.Text));
        Assert.True(blocked.Dwell > tooShort.Dwell);
    }

    [Fact]
    public void AnUnhandledStateIsShownForLongEnoughToBeReported()
    {
        // The unknown arms are diagnostics: they render a bare enum member
        // name, which means nothing to the person reading it. Six hundred
        // milliseconds of that is a flicker somebody half-saw and cannot
        // describe, so the unknown direction is the LONG dwell, not the short
        // one. Asserted through the enum's own unused range rather than by
        // adding a member.
        var unknown = DictationHudModel.For((DictationState)999);

        Assert.Equal(DictationHudModel.FailureDwell, unknown.Dwell);
        Assert.Equal(
            DictationHudModel.FailureDwell,
            DictationHudModel.For(new DictationSessionResult((DictationOutcome)999, null, null)).Dwell);
    }

    /// <summary>
    /// The point below which a change at the edge of vision is not perceived
    /// at all — the floor the receipt tier sits above rather than on.
    /// </summary>
    private static readonly TimeSpan PerceptibleFloor = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Roughly how long a pill's own sentence takes to read: the time to
    /// NOTICE something that arrived unannounced, plus the words themselves at
    /// a glancing speed.
    /// </summary>
    /// <remarks>
    /// 240 words a minute rather than a prose reading speed. These are short
    /// declarative phrases in a fixed place, which are read faster than
    /// paragraphs — and the number only has to be defensible as a FLOOR, since
    /// every tier it is applied to is allowed to be longer than it.
    /// </remarks>
    private static TimeSpan ReadingTimeFor(string text)
    {
        var words = text.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        return PerceptibleFloor + TimeSpan.FromSeconds(words / 4.0);
    }

    // ── The live states ──────────────────────────────────────────────────

    [Theory]
    [InlineData(DictationState.Listening)]
    [InlineData(DictationState.Transcribing)]
    [InlineData(DictationState.Inserting)]
    public void ABusyStateLastsAsLongAsTheStateDoes(DictationState state)
    {
        var model = DictationHudModel.For(state);

        Assert.True(model.IsVisible);
        // A timer here would dismiss "Listening" mid-sentence, which is a lie
        // about the microphone — the exact lie this HUD exists to end.
        Assert.Null(model.Dwell);
    }

    [Theory]
    [InlineData(DictationState.Idle)]
    [InlineData(DictationState.Armed)]
    public void NotDictatingShowsNothing(DictationState state)
    {
        Assert.False(DictationHudModel.For(state).IsVisible);
    }

    [Fact]
    public void ListeningIsDistinctFromWorking()
    {
        // The whole point: "the microphone is open" and "the microphone closed
        // and a recognizer has it" are different facts, and a user who let go
        // needs to see that it registered.
        Assert.NotEqual(
            DictationHudModel.For(DictationState.Listening).Text,
            DictationHudModel.For(DictationState.Transcribing).Text);
    }

    // ── What it says, and what it must never say ─────────────────────────

    [Fact]
    public void ASuccessNamesTheWindowItWentTo()
    {
        var model = DictationHudModel.For(new DictationSessionResult(
            DictationOutcome.Inserted,
            "the quick brown fox",
            FocusedApplicationResult.Resolved("slack", 42, "Slack")));

        Assert.Contains("slack", model.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASuccessWithNoResolvedWindowStillReadsAsASentence()
    {
        // No target is a real case — insertion can succeed against a window the
        // resolver could not name. "Typed into " with nothing after it is the
        // shape of a bug even when the dictation worked.
        var model = DictationHudModel.For(Result(DictationOutcome.Inserted));

        Assert.Equal("Typed", model.Text);
    }

    // ── The one-time first-success hint (UX grill Q4b) ───────────────────

    [Fact]
    public void TheFirstSuccessEverTeachesInsteadOfConfirming()
    {
        // The first "Typed" the user ever earns is the one moment the pill is
        // allowed to explain the feature's whole contract. Notice dwell, not
        // receipt dwell — this line is read, not glanced at.
        var model = DictationHudModel.For(Result(DictationOutcome.Inserted),
            firstEverInsert: true);

        Assert.Equal("That worked — it types anywhere your cursor is", model.Text);
        Assert.Equal(HudTone.Neutral, model.Tone);
        Assert.Equal(DictationHudModel.NoticeDwell, model.Dwell);
    }

    [Fact]
    public void TheHintNeverOutlivesTheFirstSuccess()
    {
        // firstEverInsert false (every later success) → the ordinary receipt.
        var model = DictationHudModel.For(Result(DictationOutcome.Inserted),
            firstEverInsert: false);

        Assert.Equal("Typed", model.Text);
        Assert.Equal(DictationHudModel.ReceiptDwell, model.Dwell);
    }

    [Fact]
    public void TheHintOnlyEverRidesASuccess()
    {
        // A failure with the flag set must render the failure, not the hint —
        // "that worked" over a paste that did not land would be the pill
        // lying about the one thing it exists to be honest about.
        var model = DictationHudModel.For(Result(DictationOutcome.NoSpeech),
            firstEverInsert: true);

        Assert.DoesNotContain("worked", model.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void ItNeverShowsWhatWasDictated(DictationOutcome outcome)
    {
        // The words go to the focused window and nowhere else. A HUD is visible
        // to whoever is behind the user and to whatever is screen-sharing.
        const string secret = "my password is hunter2";
        var model = DictationHudModel.For(new DictationSessionResult(outcome, secret, null));

        Assert.DoesNotContain("hunter2", model.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static DictationSessionResult Result(DictationOutcome outcome) =>
        new(outcome, outcome == DictationOutcome.Inserted ? "words" : null, null);

    // ─── "Listening" vs "Hearing you" ───────────────────────────────────

    [Fact]
    public void A_silent_microphone_says_Listening()
    {
        // The state the pill opens in, and the state a muted or dead microphone
        // stays in for the whole hold. Flat bars alone read as an idle HUD; the
        // word is what makes them read as "nothing is reaching me".
        Assert.Equal("Listening", DictationHudModel.Listening(0f).Text);
    }

    [Fact]
    public void A_level_above_the_gate_says_Hearing_you()
    {
        // macOS's wording, character for character. Two platforms that phrase
        // the same fact differently are two products.
        Assert.Equal(
            "Hearing you",
            DictationHudModel.Listening(DictationHudModel.AudibleLevel + 0.01f).Text);
    }

    [Fact]
    public void The_gate_itself_is_still_silence()
    {
        // Strictly greater than, matching the presenter's own gate: a level
        // exactly at the threshold is published as a hard zero, so a pill
        // saying "Hearing you" over bars drawing silence is not reachable.
        Assert.Equal(
            "Listening",
            DictationHudModel.Listening(DictationHudModel.AudibleLevel).Text);
    }

    [Fact]
    public void The_state_pill_starts_quiet()
    {
        // A state change carries no level with it — the microphone has only
        // just opened — so entering Listening must not claim to hear anything.
        Assert.Equal(
            "Listening",
            DictationHudModel.For(DictationState.Listening).Text);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void The_listening_pill_never_dismisses_itself(float level)
    {
        // Null dwell at every level. A timer that dismissed this mid-sentence
        // would be lying about the microphone, and the label switching must not
        // have quietly introduced one.
        Assert.Null(DictationHudModel.Listening(level).Dwell);
        Assert.Equal(HudTone.Neutral, DictationHudModel.Listening(level).Tone);
    }

    // ── What a screen reader hears ───────────────────────────────────────────
    //
    // The Windows twin of macOS's DictationHUDSelfCheck. That suite asserts a
    // non-empty accessibility label for every phase and every failure, and that
    // the labels are DISTINCT across states, because "the resting state must
    // announce itself, and distinctly from listening".
    //
    // Windows had none of it. DictationHudWindow is WS_EX_NOACTIVATE |
    // WS_EX_TOOLWINDOW with no AutomationProperties.Name and no LiveSetting
    // anywhere, so a Narrator user held the key and got nothing: no
    // "Listening", no "Hearing you", and no reason when nothing was typed.

    [Theory]
    [MemberData(nameof(AllStates))]
    public void EveryVisibleStateAnnouncesSomething(DictationState state)
    {
        var model = DictationHudModel.For(state);

        if (!model.IsVisible)
        {
            // Idle and Armed are Hidden by design. A hidden pill must NOT
            // announce: raising a live-region event for an empty string is how
            // a screen reader ends up saying "blank" every time a dictation
            // ends.
            Assert.False(model.ShouldAnnounce);
            Assert.Empty(model.AnnouncedText);
            return;
        }

        Assert.True(model.ShouldAnnounce);
        Assert.False(string.IsNullOrWhiteSpace(model.AnnouncedText));
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void EveryOutcomeAnnouncesSomething(DictationOutcome outcome)
    {
        // Every one, including the failures. A failure is the case where the
        // announcement is the ONLY evidence: nothing was typed, so a listener
        // has no document to check.
        var model = DictationHudModel.For(Result(outcome));

        Assert.True(model.ShouldAnnounce);
        Assert.False(string.IsNullOrWhiteSpace(model.AnnouncedText));
    }

    [Fact]
    public void TheAnnouncementIsTheSentenceOnScreen()
    {
        // A listener and a reader get the same words. The alternative is two
        // copies of this feature's vocabulary, drifting.
        foreach (var outcome in Enum.GetValues<DictationOutcome>())
        {
            var model = DictationHudModel.For(Result(outcome));
            Assert.Equal(model.Text, model.AnnouncedText);
        }
    }

    [Fact]
    public void EveryOutcomeAnnouncesSomethingDIFFERENT()
    {
        // macOS's distinctness check, ported. Two failures that announce the
        // same words are, to a listener, one failure — and the pair this would
        // most likely collapse is the pair the enum works hardest to keep
        // apart: a microphone that heard silence (NoSpeech) and a key brushed
        // on the way past (HoldTooShort).
        var announced = Enum.GetValues<DictationOutcome>()
            .Select(o => DictationHudModel.For(Result(o)).AnnouncedText)
            .ToArray();

        Assert.Equal(announced.Length, announced.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ListeningAndHearingYouAnnounceDifferently()
    {
        // The resting state must announce itself, and distinctly from
        // listening -- macOS's own words. On Windows the distinction is
        // between an open microphone that is hearing nothing and one that is,
        // and it is the answer to the only question a listener has.
        var quiet = DictationHudModel.Listening(0f);
        var loud = DictationHudModel.Listening(0.5f);

        Assert.NotEqual(quiet.AnnouncedText, loud.AnnouncedText);
        Assert.False(string.IsNullOrWhiteSpace(quiet.AnnouncedText));
    }

    [Fact]
    public void ThePhasesAnnounceDistinctlyFromEachOther()
    {
        var announced = Enum.GetValues<DictationState>()
            .Select(DictationHudModel.For)
            .Where(m => m.IsVisible)
            .Select(m => m.AnnouncedText)
            .ToArray();

        Assert.Equal(announced.Length, announced.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void WhatWasDictatedIsNeverSpokenAloud()
    {
        // The pill never renders the user's words, so it can never announce
        // them either -- and the announcement is the visible sentence and
        // nothing more, which is the property that keeps it that way. This
        // was first written about the live hypothesis; the hypothesis is gone
        // and the rule is not, because the next thing tempted to put a
        // transcript on this surface will be announced by exactly this path.
        var model = DictationHudModel.Listening(0.5f);

        Assert.Equal("Hearing you", model.AnnouncedText);
        Assert.Equal(model.Text, model.AnnouncedText);
    }

    [Fact]
    public void TheSecureFieldRefusalIsAnnouncedWithItsReason()
    {
        // macOS asserts the label reads title AND detail together, so a
        // listener is never told that something failed without being told
        // what to do about it. On Windows the two halves are one sentence, so
        // the property to hold is that the actionable half survives.
        var model = DictationHudModel.For(Result(DictationOutcome.SecureField));

        Assert.Contains("password field", model.AnnouncedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing was recorded", model.AnnouncedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBlockedMicrophoneAnnouncesWhereTheSwitchIs()
    {
        var model = DictationHudModel.For(Result(DictationOutcome.MicrophoneBlocked));

        Assert.Contains("Settings", model.AnnouncedText, StringComparison.Ordinal);
    }

    // -- The pending hide, and what must not take down the wrong pill --------
    //
    // Shortening the receipt to 600 ms moves the hide from "long after the
    // user has moved on" to "possibly while they are already holding the key
    // down again". Everything below is about that window.

    [Fact]
    public void AFinishedPillArmsItsOwnHide()
    {
        var delay = new HandCrankedDelay();
        var hides = 0;
        var dwell = new DictationHudDwell(delay, () => hides++);

        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.Inserted)));

        Assert.True(dwell.HideIsPending);
        Assert.Equal(DictationHudModel.ReceiptDwell, delay.Last.Delay);
        Assert.Equal(0, hides);

        delay.Last.Fire();

        Assert.Equal(1, hides);
        Assert.False(dwell.HideIsPending);
    }

    [Fact]
    public void ALiveStateArmsNothingAtAll()
    {
        // Null dwell, at every level and in every phase. A hide armed for
        // "Listening" would dismiss the pill mid-sentence, which is the lie
        // this HUD exists to end.
        var delay = new HandCrankedDelay();
        var dwell = new DictationHudDwell(delay, () => Assert.Fail("nothing to hide"));

        dwell.Showing(DictationHudModel.For(DictationState.Listening));
        dwell.Showing(DictationHudModel.Listening(0.8f));
        dwell.Showing(DictationHudModel.For(DictationState.Transcribing));

        Assert.False(dwell.HideIsPending);
        Assert.Empty(delay.Scheduled);
    }

    [Fact]
    public void ANewDictationCancelsThePreviousOnesPendingHide()
    {
        // The overlap case, and the reason the receipt can be short. The user
        // reads "Typed into Slack", starts talking again inside 600 ms, and the
        // pill that must survive is the new one.
        var delay = new HandCrankedDelay();
        var hides = 0;
        var dwell = new DictationHudDwell(delay, () => hides++);

        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.Inserted)));
        var receipt = delay.Last;

        dwell.Showing(DictationHudModel.For(DictationState.Listening));

        Assert.True(receipt.Cancelled);
        Assert.False(dwell.HideIsPending);
        Assert.Equal(0, hides);
    }

    [Fact]
    public void ACancelledTimerThatFiresAnywayHidesNothing()
    {
        // Cancelling is not a guarantee: a DispatcherTimer whose tick is
        // already queued delivers that tick after it has been stopped. Without
        // the generation check that late arrival takes down the pill of the
        // dictation that is currently running.
        var delay = new HandCrankedDelay();
        var hides = 0;
        var dwell = new DictationHudDwell(delay, () => hides++);

        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.Inserted)));
        var stale = delay.Last;

        dwell.Showing(DictationHudModel.For(DictationState.Listening));
        stale.Fire();

        Assert.Equal(0, hides);
    }

    [Fact]
    public void AStaleTimerCannotHideTheNEXTDictationsReceiptEither()
    {
        // The nastier shape of the same race: the second dictation finishes
        // and arms its OWN hide, and only then does the first one's stopped
        // timer arrive. It must not spend the second one's dwell.
        var delay = new HandCrankedDelay();
        var hides = 0;
        var dwell = new DictationHudDwell(delay, () => hides++);

        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.Inserted)));
        var first = delay.Last;

        dwell.Showing(DictationHudModel.For(DictationState.Listening));
        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.Inserted)));
        var second = delay.Last;

        first.Fire();

        Assert.Equal(0, hides);
        Assert.True(dwell.HideIsPending);

        second.Fire();

        Assert.Equal(1, hides);
    }

    [Fact]
    public void AHideThatHasRunCannotRunTwice()
    {
        var delay = new HandCrankedDelay();
        var hides = 0;
        var dwell = new DictationHudDwell(delay, () => hides++);

        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.NoSpeech)));
        delay.Last.Fire();
        delay.Last.Fire();

        Assert.Equal(1, hides);
    }

    [Fact]
    public void APillIsNeverSTRANDEDByTheCancelling()
    {
        // The other direction, and the one a cancel-happy implementation gets
        // wrong: every visible model that carries a dwell must leave a hide
        // armed when it is done, or the pill sits on top of every window until
        // the next dictation -- which on a feature nobody uses twice in a row
        // means forever.
        foreach (var outcome in Enum.GetValues<DictationOutcome>())
        {
            var delay = new HandCrankedDelay();
            var dwell = new DictationHudDwell(delay, () => { });

            dwell.Showing(DictationHudModel.For(DictationState.Listening));
            dwell.Showing(DictationHudModel.For(Result(outcome)));

            Assert.True(dwell.HideIsPending, $"{outcome} left no way to dismiss itself");
        }
    }

    [Fact]
    public void DisposingForgetsThePendingHide()
    {
        // The presenter disposes on shutdown, before the window closes. A
        // callback still armed at that point runs against a closed window.
        var delay = new HandCrankedDelay();
        var hides = 0;
        var dwell = new DictationHudDwell(delay, () => hides++);

        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.Inserted)));
        var pending = delay.Last;
        dwell.Dispose();

        Assert.True(pending.Cancelled);

        pending.Fire();

        Assert.Equal(0, hides);
    }

    [Fact]
    public void TheHiddenModelIsAlwaysJustACancel()
    {
        var delay = new HandCrankedDelay();
        var dwell = new DictationHudDwell(delay, () => Assert.Fail("nothing to hide"));

        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.Inserted)));
        dwell.Showing(DictationHudModel.Hidden);

        Assert.False(dwell.HideIsPending);
        Assert.Single(delay.Scheduled);
        Assert.True(delay.Last.Cancelled);
    }

    [Fact]
    public void NoneOfThisSuiteWaitsForARealClock()
    {
        // The seam exists so these rules can be asserted without a dispatcher
        // and without sleeping. A test that proved the five-second failure
        // dwell by waiting five seconds would take five seconds, depend on one
        // machine's timer resolution, and pass for the wrong reason on a loaded
        // build agent. Pinned rather than trusted: a DictationHudDwell that
        // reached for a real timer of its own would fail here.
        var started = DateTime.UtcNow;
        var delay = new HandCrankedDelay();
        var hides = 0;
        var dwell = new DictationHudDwell(delay, () => hides++);

        dwell.Showing(DictationHudModel.For(Result(DictationOutcome.MicrophoneBlocked)));

        Assert.Equal(DictationHudModel.FailureDwell, delay.Last.Delay);

        delay.Last.Fire();

        Assert.Equal(1, hides);
        Assert.True(DateTime.UtcNow - started < DictationHudModel.FailureDwell);
    }

    /// <summary>
    /// <see cref="IDictationHudDelay"/> with the clock taken out: nothing runs
    /// until a test says so, and a cancelled delay can still be made to fire.
    /// </summary>
    /// <remarks>
    /// The second half is the point. A fake that refused to deliver a cancelled
    /// callback would be a kinder timer than the real one -- a
    /// <c>DispatcherTimer</c> whose tick is already queued delivers it after
    /// <c>Stop</c> -- and would quietly make the race this suite is about
    /// untestable.
    /// </remarks>
    private sealed class HandCrankedDelay : IDictationHudDelay
    {
        private readonly List<Pending> _scheduled = new();

        public IReadOnlyList<Pending> Scheduled => _scheduled;

        public Pending Last => _scheduled[^1];

        public IDisposable After(TimeSpan delay, Action run)
        {
            var pending = new Pending(delay, run);
            _scheduled.Add(pending);
            return pending;
        }

        internal sealed class Pending : IDisposable
        {
            private readonly Action _run;

            public Pending(TimeSpan delay, Action run)
            {
                Delay = delay;
                _run = run;
            }

            public TimeSpan Delay { get; }

            public bool Cancelled { get; private set; }

            public void Dispose() => Cancelled = true;

            /// <summary>
            /// Deliver the callback, cancelled or not. See the class remarks.
            /// </summary>
            public void Fire() => _run();
        }
    }
}
