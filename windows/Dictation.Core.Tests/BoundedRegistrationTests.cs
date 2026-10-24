using System.Collections.Concurrent;
using System.Diagnostics;
using Dictation.Services.Learning;

namespace Dictation.Core.Tests;

/// <summary>
/// The correction watch's change subscriptions, as <c>UiaSpanObserverTarget</c>
/// makes them: bounded by <c>t_uia</c>, and never left registered — not when
/// the registration finishes after the deadline, and not when its second
/// handler fails after the first went in. UI Automation itself cannot run
/// here; each step below stands for one Add…EventHandler call and its Remove.
/// </summary>
public sealed class BoundedRegistrationTests
{
    /// <summary>For a step that answers at once: long enough that a busy
    /// thread pool cannot make it miss.</summary>
    private static readonly TimeSpan InTime = TimeSpan.FromSeconds(5);

    /// <summary>For a step held until the test lets it go, which misses any deadline.</summary>
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    /// <summary>A desktop that counts what is registered on it.</summary>
    private sealed class Desktop
    {
        public ConcurrentDictionary<string, int> Registered { get; } = new();

        public ConcurrentQueue<string> Removed { get; } = new();

        /// <param name="started">Set when the step begins, before it waits on <paramref name="slow"/>.</param>
        public Func<IDisposable> Step(
            string name, ManualResetEventSlim? slow = null, bool fails = false, ManualResetEventSlim? started = null) => () =>
        {
            started?.Set();
            slow?.Wait(TimeSpan.FromSeconds(10));
            if (fails)
            {
                throw new InvalidOperationException("the provider refused");
            }

            Registered.AddOrUpdate(name, 1, (_, n) => n + 1);
            return BoundedRegistration.UndoWith(() =>
            {
                Registered.AddOrUpdate(name, 0, (_, n) => n - 1);
                Removed.Enqueue(name);
            });
        };

        public int Live => Registered.Values.Sum();
    }

    /// <summary>A registration whose provider went away: undoing it throws.</summary>
    private sealed class Gone : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("the provider is gone");
    }

    [Fact]
    public void A_registration_in_time_is_returned_and_undone_once_last_step_first()
    {
        var desktop = new Desktop();

        var r = BoundedRegistration.Register([desktop.Step("text"), desktop.Step("focus")], InTime);

        Assert.True(r.Ok);
        Assert.Equal(2, desktop.Live);
        r.Value!.Dispose();
        r.Value.Dispose();
        Assert.Equal(0, desktop.Live);
        Assert.Equal(["focus", "text"], desktop.Removed);
    }

    [Fact]
    public void A_registration_that_finishes_after_the_deadline_is_undone_when_it_lands()
    {
        var desktop = new Desktop();
        using var slow = new ManualResetEventSlim();

        var r = BoundedRegistration.Register([desktop.Step("text"), desktop.Step("focus", slow)], Short);

        Assert.False(r.Ok);
        Assert.Equal(SpanObserverFailures.UiaTimeout, r.Failure);

        // It lands after the caller gave up and went on to poll.
        slow.Set();
        Assert.True(SpinWait.SpinUntil(() => desktop.Removed.Count == 2, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, desktop.Live);
    }

    [Fact]
    public void A_second_step_that_fails_undoes_the_first()
    {
        var desktop = new Desktop();

        var r = BoundedRegistration.Register([desktop.Step("text"), desktop.Step("focus", fails: true)], InTime);

        Assert.False(r.Ok);
        Assert.Equal(SpanObserverFailures.Exception, r.Failure);
        Assert.Equal(0, desktop.Live);
        Assert.Equal(["text"], desktop.Removed);
    }

    [Fact]
    public void A_late_second_step_that_fails_undoes_the_first_after_the_deadline()
    {
        var desktop = new Desktop();
        using var slow = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();

        var r = BoundedRegistration.Register(
            [desktop.Step("text"), desktop.Step("focus", slow, fails: true, started: second)], Short);
        Assert.Equal(SpanObserverFailures.UiaTimeout, r.Failure);
        // The steps run on the pool, which a busy run may not have reached by
        // the deadline. Once the second step is under way the first is in,
        // and the deadline has undone nothing.
        Assert.True(second.Wait(TimeSpan.FromSeconds(10)), "the second step never started");
        Assert.Equal(1, desktop.Live);

        slow.Set();
        Assert.True(SpinWait.SpinUntil(() => desktop.Live == 0, TimeSpan.FromSeconds(10)));
        Assert.Equal(["text"], desktop.Removed);
    }

    [Fact]
    public void A_step_that_names_its_failure_is_reported_by_name()
    {
        var r = BoundedRegistration.Register(
            [() => throw new SpanObserverCallException(SpanObserverFailures.NoTextPattern)], InTime);

        Assert.Equal(SpanObserverFailures.NoTextPattern, r.Failure);
    }

    [Fact]
    public void Undoing_a_provider_that_does_not_answer_does_not_hold_up_the_caller()
    {
        using var hung = new ManualResetEventSlim();
        var r = BoundedRegistration.Register(
            [() => BoundedRegistration.UndoWith(() => hung.Wait(TimeSpan.FromSeconds(10)))], TimeSpan.FromSeconds(1));
        Assert.True(r.Ok);

        var watch = Stopwatch.StartNew();
        r.Value!.Dispose();
        watch.Stop();
        hung.Set();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"disposing took {watch.Elapsed}");
    }

    [Fact]
    public void The_app_registers_its_ui_automation_handlers_only_through_it()
    {
        // The Windows adapter cannot run here. This keeps it on the helper the
        // tests above hold to account: each handler it adds is a step of one
        // BoundedRegistration.Register call, with an undo, and there is no
        // other place in the file that adds one.
        var source = File.ReadAllText(
            Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", "UiaSpanObserverTarget.cs"));
        var start = source.IndexOf("BoundedRegistration.Register(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Subscribe does not register through BoundedRegistration");
        var call = source[start..source.IndexOf("_tUia);", start, StringComparison.Ordinal)];

        foreach (var add in new[] { "Automation.AddAutomationEventHandler(", "Automation.AddAutomationFocusChangedEventHandler(" })
        {
            Assert.Equal(1, Occurrences(source, add));
            Assert.Contains(add, call, StringComparison.Ordinal);
        }

        Assert.Equal(2, Occurrences(call, "BoundedRegistration.UndoWith("));
    }

    private static int Occurrences(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void An_undo_that_throws_still_undoes_the_rest()
    {
        var desktop = new Desktop();
        var r = BoundedRegistration.Register([desktop.Step("text"), () => new Gone()], InTime);

        r.Value!.Dispose();

        Assert.Equal(0, desktop.Live);
    }
}
