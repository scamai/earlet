using System.Text.Json.Serialization;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>The worker's <c>ping</c> op (<c>asr_server.py</c>): answered
/// between two frames, so a worker that answers is alive and not wedged.</summary>
public sealed record AsrPingRequest
{
    [JsonPropertyName("op")]
    public string Op => "ping";
}

// Part of DictationWorkerSlot (see DictationWorkerSlot.cs): a decode the
// worker lost, tried once more, and the health check a long take makes
// while the user speaks (LIMIT 2026-10-10, "faults keep the take").
public sealed partial class DictationWorkerSlot
{
    /// <summary>
    /// A decode's worker died under it or missed its deadline, and the slot is
    /// starting it again to try once more. Raised on the decode's own thread,
    /// so a listener posts it to its own.
    /// </summary>
    public event EventHandler? DecodeRetried;

    /// <summary>
    /// One decode, and one retry when the worker lost the first: it died under
    /// it (the reply never came, or the pipe broke) or missed the deadline,
    /// either way leaving no worker alive behind it. The retry's exchange
    /// starts a new process and loads the model first, as every exchange on a
    /// dead worker does, and has the longer deadline. A worker that answered
    /// with a refusal is not retried: it is alive and said no, and would say
    /// it again. Neither is a decode that found no worker at all
    /// (<see cref="TranscriptionOutcome.RecognizerUnavailable"/>): its
    /// exchange already tried to start one. The audio stays in the spool
    /// either way, and a second failure is offered for recovery by the
    /// controller.
    /// </summary>
    private async Task<TranscriptionResult> TranscribeWithRetryAsync(MicrophoneCapture capture, CancellationToken ct)
    {
        var (first, worker) = await TranscribeOnWorkerAsync(capture, ct).ConfigureAwait(false);
        if (first.Outcome != TranscriptionOutcome.RecognizerFailed || worker is null || worker.Wire.IsReady)
        {
            return first;
        }

        _log.LogWarning(
            "The speech worker stopped or missed its deadline on {Seconds:0.#} s of audio; "
            + "starting it again and trying once more with a longer deadline",
            capture.Duration.TotalSeconds);
        try
        {
            DecodeRetried?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            // A listener's failure costs the notice, not the retry.
            _log.LogDebug(error, "A listener to the decode's retry threw");
        }

        var budget = AsrWorkerTranscriber.RetryTimeoutFor(Plan.Transcriber, capture.Duration);
        var (second, _) = await TranscribeOnWorkerAsync(capture, ct, budget).ConfigureAwait(false);
        _log.LogInformation("The retried decode ended {Outcome}", second.Outcome);
        return second;
    }

    /// <summary>
    /// Whether the worker is alive, asked while a long take is recorded: a
    /// <c>ping</c> on the installed worker, answered within
    /// <paramref name="timeout"/>. A worker that died is started again by the
    /// exchange itself before it is asked, which is the restart a long take
    /// needs while the user speaks; none installed (unloaded after idle, or a
    /// start that failed) is started, or the start already running is joined.
    /// A worker loading for this hold is left to load and counts as alive.
    /// </summary>
    /// <returns>False when the worker could not be started or did not answer
    /// in time; the exchange that timed out has already stopped it, so the
    /// next check starts it again.</returns>
    public async Task<bool> CheckAsync(TimeSpan timeout, CancellationToken ct)
    {
        AsrWorkerTranscriber? current;
        Task<bool>? starting;
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            current = _current;
            starting = _starting;
        }

        if (starting is not null)
        {
            return true;
        }

        if (current is null)
        {
            try
            {
                return await StartAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _log.LogWarning("Speech recognition could not be started during a long take ({Error})", error.GetType().Name);
                return false;
            }
        }

        var wasReady = current.Wire.IsReady;
        var exchange = await current.Wire.ExchangeAsync<AsrPingRequest, AsrResponse>(
            new AsrPingRequest(), timeout, AsrWirePriority.Interactive, ct, "a health check during a long take")
            .ConfigureAwait(false);
        var alive = exchange.Status == AsrExchangeStatus.Replied && exchange.Reply is { Ok: true };
        if (alive && !wasReady)
        {
            _log.LogWarning("The speech worker had stopped during a long take; it was started again while the user spoke");
        }

        return alive;
    }
}
