using System.Diagnostics;
using Dictation.Services.Learning;

namespace Dictation.Core;

/// <summary>
/// A registration with another process that has to be undone — the UI
/// Automation change handlers the correction watch subscribes — made under a
/// deadline without leaking it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="SpanObserver.Bounded{T}"/>.</b> That helper abandons
/// a call that misses its deadline and drops the result, which is right for a
/// read and wrong for a registration: the registration still completes, and
/// the only thing that could undo it is the result nobody reads. Every slow
/// registration then stays in place until the process exits — a desktop-wide
/// focus handler making cross-process reads on every focus change, holding
/// the observation it was made for. Here the task is kept, and a registration
/// that lands after the deadline is undone as soon as it does.
/// </para>
/// <para>
/// <b>Steps.</b> A registration made of several steps that fails partway
/// undoes the steps that succeeded, so a first handler is not left behind by
/// a second that threw.
/// </para>
/// <para>
/// <b>Undoing is bounded too.</b> Disposing what <see cref="Register"/>
/// returns runs the undo under the same deadline, and a provider that does
/// not answer cannot hold up the caller.
/// </para>
/// </remarks>
public static class BoundedRegistration
{
    /// <summary>
    /// Run <paramref name="steps"/> in order on the thread pool, and wait up
    /// to <paramref name="deadline"/> for all of them.
    /// </summary>
    /// <param name="steps">Each makes one registration and returns what undoes
    /// it (see <see cref="UndoWith"/>). A step may throw
    /// <see cref="SpanObserverCallException"/> to name its failure.</param>
    /// <returns>
    /// What undoes every step, or a failure — <see cref="SpanObserverFailures.UiaTimeout"/>
    /// or the step's reason — with nothing left registered: steps that finish
    /// after the deadline are undone when they do.
    /// </returns>
    public static SpanObserverResult<IDisposable> Register(IReadOnlyList<Func<IDisposable>> steps, TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var sw = Stopwatch.StartNew();
        var task = Task.Run(() => RegisterAll(steps));
        try
        {
            if (!task.Wait(deadline))
            {
                // Not abandoned: undone the moment it lands. A late failure
                // has already undone its own steps; observing it keeps it off
                // the unobserved-task path.
                _ = task.ContinueWith(
                    static t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            t.Result.Undo();
                        }
                        else
                        {
                            _ = t.Exception;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return SpanObserverResult<IDisposable>.Failed(SpanObserverFailures.UiaTimeout, sw.Elapsed.TotalMilliseconds);
            }

            return SpanObserverResult<IDisposable>.Success(new Bounded(task.Result, deadline), sw.Elapsed.TotalMilliseconds);
        }
        catch (AggregateException ex) when (ex.InnerException is SpanObserverCallException sce)
        {
            return SpanObserverResult<IDisposable>.Failed(sce.Reason, sw.Elapsed.TotalMilliseconds);
        }
        catch (AggregateException)
        {
            return SpanObserverResult<IDisposable>.Failed(SpanObserverFailures.Exception, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>What undoes one step: <paramref name="undo"/>, once.</summary>
    public static IDisposable UndoWith(Action undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        return new Once(undo);
    }

    private static Steps RegisterAll(IReadOnlyList<Func<IDisposable>> steps)
    {
        var done = new Steps();
        try
        {
            foreach (var step in steps)
            {
                done.Add(step());
            }
        }
        catch
        {
            done.Undo();
            throw;
        }

        return done;
    }

    /// <summary>The steps that succeeded, undone last first.</summary>
    private sealed class Steps
    {
        private readonly List<IDisposable> _undo = [];

        public void Add(IDisposable undo) => _undo.Add(undo);

        public void Undo()
        {
            for (var i = _undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    _undo[i].Dispose();
                }
                catch (Exception)
                {
                    // A provider that is gone cannot be unsubscribed from;
                    // the rest are undone regardless.
                }
            }
        }
    }

    /// <summary>A registration the caller holds: undone under the deadline.
    /// Each step's own undo runs once, however often this is disposed.</summary>
    private sealed class Bounded(Steps steps, TimeSpan deadline) : IDisposable
    {
        public void Dispose() => SpanObserver.Bounded(() =>
        {
            steps.Undo();
            return true;
        }, deadline);
    }

    private sealed class Once(Action undo) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                undo();
            }
        }
    }
}
