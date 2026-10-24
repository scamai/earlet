namespace Dictation.Core;

/// <summary>
/// Runs work one item at a time, in the order it was queued, on the thread
/// pool. The Vocabulary section's file work goes through one of these
/// (<see cref="VocabularyModel"/>), for two reasons:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Off the UI thread.</b> Every vocabulary change writes
/// <c>learning.json</c> and flushes it to disk. The low-level keyboard hook
/// runs on the WPF UI thread, and Windows drops a hook that keeps it waiting,
/// so a click does not run its disk flush on that thread. It can still wait
/// on one: a repaint reads the list under the learning lock, which a write
/// holds through its flush, so a second click or a repaint that lands during
/// a slow write waits for it.</item>
/// <item><b>In order.</b> Two clicks reach the file in the order they were
/// made. Add "SkyDock" then Remove it must not run the other way round and
/// leave the word in the list.</item>
/// </list>
/// <para>An item that throws does not stop the items queued after it; its
/// own task is faulted.</para>
/// </remarks>
public sealed class SerialWorkQueue
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Queue <paramref name="work"/> behind everything queued before it.</summary>
    /// <returns>Completes once it has run; faulted when it threw.</returns>
    public Task Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            // Not OnlyOnRanToCompletion: a fault ahead must not strand the rest.
            _tail = _tail.ContinueWith(
                _ => work(),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
            return _tail;
        }
    }
}
