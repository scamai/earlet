using System.Text.Json;
using Dictation.Speech;

namespace Dictation.Core.Tests;

public sealed class WorkerProcessFixtureTests
{
    [Fact]
    public async Task Stopping_an_old_process_cannot_stop_its_replacement()
    {
        using var worker = new FakeSpeechWorker();
        using var old = worker.CreateProcess();
        await old.StartAsync(CancellationToken.None);
        await Init(old);

        using var replacement = worker.CreateProcess();
        await replacement.StartAsync(CancellationToken.None);
        await Init(replacement);
        await old.StopAsync(TimeSpan.Zero, CancellationToken.None);
        old.Dispose();

        Assert.False(old.IsAlive);
        Assert.True(replacement.IsAlive);
        Assert.True(worker.IsAlive);
        Assert.Equal(2, worker.Starts);
        await Init(replacement);
    }

    private static async Task Init(IAsrWorkerProcess worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await AsrFraming.WriteFrameAsync(worker.StandardIn, new { op = "init" }, timeout.Token);
        using var reply = await AsrFraming.ReadFrameAsync<JsonDocument>(worker.StandardOut, timeout.Token);
        Assert.NotNull(reply);
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
    }
}
