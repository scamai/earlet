using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

/// <summary>
/// The per-attempt receipt on its own: the Mac's
/// <c>AttemptDiagnosticsTests</c> (<c>measuredReceipt</c>,
/// <c>privateFailure</c>) ported, the fixed table of endings, the recovery and
/// copy lines that follow an ending, which attempt a result belongs to, and
/// the launch identity
/// (<c>PermissionChecklistTests.installationIdentity</c>). The real controller
/// driving it is in <see cref="DictationAttemptWiringTests"/>.
/// </summary>
public sealed class DictationAttemptLogTests
{
    /// <summary>Every ending a healthy receipt may carry, as the docs list
    /// them. The bug path's <see cref="DictationAttemptReceipt.Unclassified"/>
    /// is left out on purpose.</summary>
    internal static readonly Regex AllowedEnding = new(
        "^result=(inserted clipboard_unchanged=(true|false) restore_detached=false"
        + "|failed stage=(capture|asr|paste) reason=(hold_too_short|microphone_permission|microphone_unavailable"
        + "|empty_capture|secure_field|released_before_capture|no_text|recognition_failed|no_field|paste_refused)"
        + "|command_completed|cancelled|shortcut|privacy_refused|still_finishing|still_copying|engine_unavailable)$");

    /// <summary>The lines that may follow an ending: a refused paste's
    /// recovery and each Copy last text of the words the attempt left.</summary>
    internal static readonly Regex AllowedAfterEnding = new(
        "^dictation attempt=[0-9a-f-]+ event=(recovery clipboard=(true|false) available_in_app=(true|false)"
        + "|copy result=(copied|failed available_in_app=true))$");

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    // ─── The receipt (AttemptDiagnosticsTests) ──────────────────────────

    [Fact]
    public void One_final_receipt_includes_measured_release_time_and_never_dictated_content()
    {
        // measuredReceipt.
        var log = new MemoryLog();
        var now = Seconds(10);
        var receipt = new DictationAttemptLog(log.CreateLogger("Test"), () => now);
        now = Seconds(12);
        receipt.MarkReleased();
        now = Seconds(12.25);
        receipt.Finish(new DictationSessionResult(
            DictationOutcome.Inserted,
            "private spoken words",
            FocusedApplicationResult.Resolved("privatewriter", 7, "Private Plans - Editor"),
            DecodeDuration: TimeSpan.FromMilliseconds(125),
            DetectedLanguage: "private metadata",
            NBest: ["private alternative"]));
        receipt.Finish(DictationAttemptEnding.Cancelled);

        Assert.Equal(3, log.Lines.Count);
        Assert.Equal($"dictation attempt={receipt.Id} event=started", log.Lines[0]);
        Assert.Equal($"dictation attempt={receipt.Id} event=released", log.Lines[1]);
        var ending = log.Lines[^1];
        Assert.Contains("event=finished result=inserted clipboard_unchanged=true restore_detached=false", ending);
        Assert.Contains("elapsed_ms=2250 release_ms=250", ending);
        Assert.Contains("asr_ms=125 paste_ms=n/a", ending);
        Assert.DoesNotContain("private", string.Join("\n", log.Lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failure_payloads_and_unmeasured_durations_are_not_logged_as_content_or_zero()
    {
        // privateFailure. A Windows result carries no error text; what it can
        // carry is the refused words and the window they were aimed at.
        var log = new MemoryLog();
        var receipt = new DictationAttemptLog(log.CreateLogger("Test"), () => Seconds(1));
        receipt.Finish(new DictationSessionResult(DictationOutcome.RecognizerFailed, null, null));
        var ending = log.Lines[^1];
        Assert.Contains("stage=asr reason=recognition_failed", ending);
        Assert.Contains("asr_ms=n/a paste_ms=n/a", ending);
        Assert.Contains("release_ms=n/a", ending);

        var refused = new MemoryLog();
        new DictationAttemptLog(refused.CreateLogger("Test"), () => Seconds(1)).Finish(new DictationSessionResult(
            DictationOutcome.InsertionRefused,
            null,
            FocusedApplicationResult.Resolved("privatewriter", 7, "Private Plans - Editor"),
            InsertDuration: TimeSpan.FromMilliseconds(40),
            UndeliveredText: "private undelivered words"));
        Assert.Contains("result=failed stage=paste reason=paste_refused asr_ms=n/a paste_ms=40", refused.Lines[^1]);
        Assert.DoesNotContain("private", string.Join("\n", refused.Lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_release_after_the_ending_and_a_second_release_are_not_logged()
    {
        var log = new MemoryLog();
        var now = Seconds(1);
        var receipt = new DictationAttemptLog(log.CreateLogger("Test"), () => now);
        receipt.MarkReleased();
        now = Seconds(2);
        receipt.MarkReleased();
        receipt.Finish(DictationAttemptEnding.Cancelled);
        receipt.MarkReleased();

        Assert.Equal(["started", "released", "finished"], log.Lines.Select(Event));
        Assert.EndsWith("release_ms=1000", log.Lines[^1], StringComparison.Ordinal);

        // Cancelled with the key still down: the key coming up afterwards is
        // not this attempt's release.
        var cancelled = new MemoryLog();
        var held = new DictationAttemptLog(cancelled.CreateLogger("Test"), () => now);
        held.Finish(DictationAttemptEnding.Cancelled);
        held.MarkReleased();
        Assert.Equal(["started", "finished"], cancelled.Lines.Select(Event));
    }

    // ─── After the ending: recovery and Copy last text ──────────────────

    /// <summary>The Mac's <c>recovery(clipboard:retained:)</c> line, and its
    /// "recent text copy" outcome as a line of the same receipt.</summary>
    [Fact]
    public void A_refused_paste_s_recovery_and_each_copy_follow_its_ending_under_its_id_in_the_Mac_s_lowercase()
    {
        var log = new MemoryLog();
        var receipt = new DictationAttemptLog(log.CreateLogger("Test"), () => Seconds(1));
        receipt.Finish(new DictationSessionResult(
            DictationOutcome.InsertionRefused, null, null, UndeliveredText: "private undelivered words"));
        receipt.Recovery(clipboard: false, availableInApp: true);
        receipt.Copied(false);
        receipt.Copied(true);
        receipt.Recovery(clipboard: true, availableInApp: false);

        Assert.Equal(
        [
            $"dictation attempt={receipt.Id} event=recovery clipboard=false available_in_app=true",
            $"dictation attempt={receipt.Id} event=copy result=failed available_in_app=true",
            $"dictation attempt={receipt.Id} event=copy result=copied",
            $"dictation attempt={receipt.Id} event=recovery clipboard=true available_in_app=false",
        ], log.Lines.Skip(2));
        Assert.All(log.Lines.Skip(2), l => Assert.Matches(AllowedAfterEnding, l));
        Assert.DoesNotContain("private", string.Join("\n", log.Lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_press_during_a_copy_has_its_own_ending()
    {
        Assert.Equal("result=still_copying", DictationAttemptReceipt.For(DictationAttemptEnding.StillCopying));

        var log = new MemoryLog();
        var attempts = new DictationAttemptTracker(log.CreateLogger("Test"), () => Seconds(1));
        attempts.Pressed();
        attempts.PressEnded(DictationAttemptEnding.StillCopying);
        attempts.KeyUp();

        Assert.Equal(["started", "finished"], log.Lines.Select(Event));
        Assert.Contains("event=finished result=still_copying asr_ms=n/a paste_ms=n/a", log.Lines[^1]);
    }

    /// <summary>The receipt the session's result ended goes on with the
    /// result, for its recovery and copy lines; with no hold admitted there
    /// is none.</summary>
    [Fact]
    public void The_result_s_receipt_is_handed_on_after_its_ending()
    {
        var log = new MemoryLog();
        var attempts = new DictationAttemptTracker(log.CreateLogger("Test"), () => Seconds(1));
        Assert.Null(attempts.Completed(new DictationSessionResult(DictationOutcome.Inserted, "words", null), TriggerCancel.None));

        attempts.Pressed();
        attempts.Admitted();
        var receipt = attempts.Completed(
            new DictationSessionResult(DictationOutcome.InsertionRefused, null, null), TriggerCancel.None);

        Assert.NotNull(receipt);
        Assert.Equal(Attempt(log.Lines[^1]), receipt.Id.ToString());
        Assert.Null(attempts.Completed(new DictationSessionResult(DictationOutcome.Inserted, "words", null), TriggerCancel.None));
    }

    // ─── The fixed table ────────────────────────────────────────────────

    [Fact]
    public void Every_session_outcome_has_a_fixed_ending_from_the_documented_list()
    {
        foreach (var outcome in Enum.GetValues<DictationOutcome>())
        {
            var ending = DictationAttemptReceipt.For(outcome);
            Assert.True(AllowedEnding.IsMatch(ending), $"{outcome} -> '{ending}'");
        }
    }

    [Fact]
    public void Every_controller_ending_but_the_bug_path_is_named()
    {
        foreach (var ending in Enum.GetValues<DictationAttemptEnding>().Where(e => e != DictationAttemptEnding.Unclassified))
        {
            Assert.True(AllowedEnding.IsMatch(DictationAttemptReceipt.For(ending)), ending.ToString());
        }

        Assert.Equal(DictationAttemptReceipt.Unclassified, DictationAttemptReceipt.For(DictationAttemptEnding.Unclassified));
        Assert.Equal(DictationAttemptReceipt.Unclassified, DictationAttemptReceipt.For((DictationOutcome)99));
    }

    [Theory]
    [InlineData(DictationOutcome.Inserted, "result=inserted clipboard_unchanged=true restore_detached=false")]
    [InlineData(DictationOutcome.ClipboardRestoreFailed, "result=inserted clipboard_unchanged=false restore_detached=false")]
    [InlineData(DictationOutcome.HoldTooShort, "result=failed stage=capture reason=hold_too_short")]
    [InlineData(DictationOutcome.MicrophoneBlocked, "result=failed stage=capture reason=microphone_permission")]
    [InlineData(DictationOutcome.MicrophoneUnavailable, "result=failed stage=capture reason=microphone_unavailable")]
    [InlineData(DictationOutcome.NoAudio, "result=failed stage=capture reason=empty_capture")]
    [InlineData(DictationOutcome.SecureField, "result=failed stage=capture reason=secure_field")]
    [InlineData(DictationOutcome.NoSpeech, "result=failed stage=asr reason=no_text")]
    [InlineData(DictationOutcome.RecognizerUnavailable, "result=engine_unavailable")]
    [InlineData(DictationOutcome.RecognizerFailed, "result=failed stage=asr reason=recognition_failed")]
    [InlineData(DictationOutcome.NoFocusedWindow, "result=failed stage=paste reason=no_field")]
    [InlineData(DictationOutcome.InsertionRefused, "result=failed stage=paste reason=paste_refused")]
    [InlineData(DictationOutcome.RanWorkflow, "result=command_completed")]
    public void Each_outcome_reads_as_the_Mac_names_it_or_as_its_Windows_reason(DictationOutcome outcome, string ending) =>
        Assert.Equal(ending, DictationAttemptReceipt.For(outcome));

    [Theory]
    [InlineData(TriggerCancel.Chord, "result=shortcut")]
    [InlineData(TriggerCancel.Escape, "result=cancelled")]
    [InlineData(TriggerCancel.None, "result=cancelled")]
    public void A_chord_cancel_is_the_shortcut_and_every_other_cancel_a_cancel(TriggerCancel by, string ending) =>
        Assert.Equal(ending, DictationAttemptReceipt.For(DictationOutcome.Cancelled, by));

    // ─── Which attempt a result belongs to ──────────────────────────────

    [Fact]
    public void An_admitted_hold_gets_the_session_s_result_once_and_a_late_one_is_dropped()
    {
        var log = new MemoryLog();
        var attempts = new DictationAttemptTracker(log.CreateLogger("Test"), () => Seconds(1));
        attempts.Pressed();
        attempts.Admitted();
        attempts.Released();
        attempts.Completed(new DictationSessionResult(DictationOutcome.Inserted, "words", null), TriggerCancel.None);
        attempts.Completed(new DictationSessionResult(DictationOutcome.Cancelled, null, null), TriggerCancel.None);
        attempts.Abandon();

        Assert.Equal(["started", "released", "finished"], log.Lines.Select(Event));
        Assert.Contains("result=inserted", log.Lines[^1]);
    }

    [Fact]
    public void A_hold_the_session_never_answered_does_not_take_the_next_hold_s_result()
    {
        var log = new MemoryLog();
        var attempts = new DictationAttemptTracker(log.CreateLogger("Test"), () => Seconds(1));
        attempts.Pressed();
        attempts.Admitted();
        attempts.Pressed();
        attempts.Admitted();
        attempts.Completed(new DictationSessionResult(DictationOutcome.Inserted, "words", null), TriggerCancel.None);

        var endings = log.Lines.Where(l => Event(l) == "finished").ToList();
        Assert.Equal(2, endings.Count);
        Assert.Contains(DictationAttemptReceipt.Unclassified, endings[0]);
        Assert.Contains("result=inserted", endings[1]);
        Assert.NotEqual(Attempt(endings[0]), Attempt(endings[1]));
    }

    [Fact]
    public void A_press_whose_check_never_answered_ends_when_the_next_press_begins()
    {
        var log = new MemoryLog();
        var attempts = new DictationAttemptTracker(log.CreateLogger("Test"), () => Seconds(1));
        attempts.Pressed();
        attempts.KeyUp();
        attempts.Pressed();
        attempts.PressEnded(DictationAttemptEnding.PrivacyRefused);

        Assert.Equal(["started", "released", "finished", "started", "finished"], log.Lines.Select(Event));
        Assert.Contains("reason=released_before_capture", log.Lines[2]);
        Assert.Contains("result=privacy_refused", log.Lines[^1]);
    }

    [Fact]
    public void An_ended_press_cannot_be_admitted()
    {
        var log = new MemoryLog();
        var attempts = new DictationAttemptTracker(log.CreateLogger("Test"), () => Seconds(1));
        attempts.Pressed();
        attempts.PressEnded(DictationAttemptEnding.StillFinishing);
        attempts.Admitted();
        attempts.Completed(new DictationSessionResult(DictationOutcome.Inserted, "words", null), TriggerCancel.None);

        Assert.Single(log.Lines, l => Event(l) == "finished");
        Assert.Contains("result=still_finishing", log.Lines[^1]);
    }

    [Fact]
    public void Stopping_ends_an_undecided_press_as_cancelled_and_an_unanswered_hold_as_unclassified()
    {
        // The controller cancels the session before it stops the receipts,
        // and that cancel answers a hold that is still live. A hold still
        // admitted here is one the session went back to rest from without a
        // result (its pipeline threw): a bug, not a cancel the user made.
        var log = new MemoryLog();
        var attempts = new DictationAttemptTracker(log.CreateLogger("Test"), () => Seconds(1));
        attempts.Pressed();
        attempts.Admitted();
        var hold = Attempt(log.Lines[^1]);
        attempts.Pressed();
        var press = Attempt(log.Lines[^1]);
        attempts.Abandon();
        attempts.Abandon();

        var endings = log.Lines.Where(l => Event(l) == "finished").ToList();
        Assert.Equal(2, endings.Count);
        Assert.Contains("result=cancelled", endings.Single(l => Attempt(l) == press));
        Assert.Contains(DictationAttemptReceipt.Unclassified, endings.Single(l => Attempt(l) == hold));
    }

    // ─── Launch identity (PermissionChecklistTests.installationIdentity) ─

    [Fact]
    public void Installation_identity_distinguishes_builds_and_names_the_running_executable()
    {
        var installed = new DictationInstallation(
            "0.1.0", "20261006120000", @"C:\Users\someone\AppData\Local\Programs\Dictation\Dictation.exe");
        Assert.Contains("20261006120000", installed.VersionLabel);
        Assert.Equal(
            @"launch Version 0.1.0 · Build 20261006120000 path=C:\Users\someone\AppData\Local\Programs\Dictation\Dictation.exe",
            installed.LaunchLine);

        Assert.Contains("Build not recorded", new DictationInstallation("0.1.0", null, "x").VersionLabel);
        Assert.Contains("Build not recorded", new DictationInstallation("0.1.0", " ", "x").VersionLabel);
    }

    [Fact]
    public void An_assembly_without_the_packager_s_stamp_reads_as_not_recorded()
    {
        var core = typeof(DictationInstallation).Assembly;
        var read = DictationInstallation.Of(core, "/opt/dictation/Dictation");

        Assert.Null(read.Build);
        Assert.Equal("/opt/dictation/Dictation", read.Path);
        Assert.False(string.IsNullOrWhiteSpace(read.Version));
        Assert.Equal("unknown", DictationInstallation.Of(core, null).Path);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    internal static string Event(string line) => Regex.Match(line, "event=(\\w+)").Groups[1].Value;

    internal static string Attempt(string line) => Regex.Match(line, "attempt=([0-9a-f-]+)").Groups[1].Value;
}

/// <summary>Every line logged through it, formatted, in order.</summary>
internal sealed class MemoryLog : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => _lines.ToArray();

    /// <summary>The attempt receipts only.</summary>
    public IReadOnlyList<string> Receipts => Lines.Where(l => l.StartsWith("dictation attempt=", StringComparison.Ordinal)).ToArray();

    /// <summary>How many results the session has raised: its own line,
    /// written after it went back to rest and raised the result.</summary>
    public int Raised => Lines.Count(l => l.StartsWith("Dictation completed:", StringComparison.Ordinal));

    /// <summary>
    /// Wait, without draining the controller's posts, until the session has
    /// raised <paramref name="count"/> results. On Windows the session goes
    /// back to rest and raises its result in one call on the UI thread, so no
    /// post runs between the two. Over the fakes a held paste returns on a
    /// pool thread, and a drain in that gap runs the state's post before the
    /// result's is even queued: a test that depends on their order waits here
    /// first.
    /// </summary>
    public void AwaitRaised(int count = 1)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (Raised < count)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), $"{Raised} of {count} results raised");
            Thread.Sleep(5);
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(_lines);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
    }
}
