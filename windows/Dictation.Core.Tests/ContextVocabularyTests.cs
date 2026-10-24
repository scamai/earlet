using System.Text.Json;
using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

public sealed class ContextVocabularyTests : IDisposable
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    private readonly InstalledCopy _copy = new();
    private readonly FakePlatform _platform = new();

    [Theory]
    [InlineData("WindowsTerminal.exe", "terminal")]
    [InlineData("WeChat", "wechat")]
    [InlineData("Weixin.exe", "wechat")]
    [InlineData("notepad.exe", "other")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Application_names_map_to_bounded_scenes(string? processName, string? expected)
    {
        Assert.Equal(expected, LearningScene.ForApplication(processName));
    }

    [Fact]
    public void Evidence_scene_is_first_record_only_preserved_on_revision_and_removed_on_retraction()
    {
        var dir = LearningGoldenFileTests.TempDirectory();
        var store = new LearningStore(dir);
        var learning = new DictationLearning(store, new WordListLexicon(LearningGoldenTests.SharedLexicon));

        learning.Observe("d1", "ask javis to deploy the sever", "ask Jarvis to deploy the server", T, "terminal");
        Assert.Equal("terminal", EvidenceScene(store));

        var restarted = new DictationLearning(new LearningStore(dir), new WordListLexicon(LearningGoldenTests.SharedLexicon));
        restarted.Observe("d1", "ask javis to deploy the sever", "ask Jarvis to deploy the server", T.AddSeconds(1), "wechat");
        Assert.Equal("terminal", EvidenceScene(new LearningStore(dir)));

        restarted.Observe("d1", "ask javis to deploy the sever", "ask javis to deploy the sever", T.AddSeconds(2), null);
        Assert.Empty(Evidence(new LearningStore(dir)).Observations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public void Unknown_or_missing_scene_is_omitted_from_evidence(string? scene)
    {
        var dir = LearningGoldenFileTests.TempDirectory();
        var store = new LearningStore(dir);
        var learning = new DictationLearning(store, new WordListLexicon(LearningGoldenTests.SharedLexicon));

        learning.Observe("d1", "ask javis to deploy the sever", "ask Jarvis to deploy the server", T, scene);

        var observation = Assert.Single(Evidence(store).Observations);
        Assert.False(observation.Extra?.ContainsKey("scene") == true);
    }

    [Fact]
    public async Task Controller_captures_target_scene_at_press_before_focus_moves()
    {
        using var rig = new Rig();
        rig.Platform.Target.Next = FocusedApplicationResult.Resolved("WindowsTerminal.exe", 42, "terminal");
        await rig.EnableAsync();

        rig.Press();
        rig.Platform.Target.Next = FocusedApplicationResult.Resolved("WeChat.exe", 43, "wechat");
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));

        var request = TranscribeRequest(rig.Platform.Worker.Frames);
        Assert.Equal("terminal", Scene(request));
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task Worker_slot_sends_context_on_the_wire_across_restart_and_keeps_transcripts()
    {
        using var slot = new DictationWorkerSlot(
            _platform,
            NullLoggerFactory.Instance,
            DictationWorkerPlan.Resolve(_copy.Root, _ => null));

        slot.SetVocabularyScene("terminal");
        var first = await slot.TranscribeAsync(Audio(), default);
        Assert.Equal(TranscriptionOutcome.Transcribed, first.Outcome);
        Assert.Equal("hello world", first.Text);

        slot.SetVocabularyScene("wechat");
        slot.Unload();
        var second = await slot.TranscribeAsync(Audio(), default);
        Assert.Equal(TranscriptionOutcome.Transcribed, second.Outcome);
        Assert.Equal("hello world", second.Text);

        slot.SetVocabularyScene(null);
        var third = await slot.TranscribeAsync(Audio(), default);
        Assert.Equal(TranscriptionOutcome.Transcribed, third.Outcome);
        Assert.Equal("hello world", third.Text);

        var requests = _platform.Worker.Frames
            .Select(frame => JsonDocument.Parse(frame))
            .Where(frame => frame.RootElement.GetProperty("op").GetString() == "transcribe")
            .Select(frame => frame.RootElement)
            .ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("terminal", Scene(requests[0]));
        Assert.Equal("wechat", Scene(requests[1]));
        Assert.Null(Scene(requests[2]));
    }

    private static MicrophoneCapture Audio() => new(new short[16_000], 16_000, 1, TimeSpan.FromSeconds(1));

    private static LearningEvidence Evidence(LearningStore store)
    {
        var state = store.Load().State;
        Assert.True(state.Extra!.TryGetValue("evidence", out var raw));
        return Assert.IsType<LearningEvidence>(raw.Deserialize<LearningEvidence>());
    }

    private static string? EvidenceScene(LearningStore store)
    {
        var observation = Assert.Single(Evidence(store).Observations);
        return observation.Extra?.TryGetValue("scene", out var value) == true ? value.GetString() : null;
    }

    private static string? Scene(JsonElement request) =>
        request.TryGetProperty("vocabulary_scene", out var value) ? value.GetString() : null;

    private static JsonElement TranscribeRequest(IEnumerable<string> frames) =>
        frames.Select(frame => JsonDocument.Parse(frame).RootElement)
            .Single(frame => frame.GetProperty("op").GetString() == "transcribe");

    public void Dispose()
    {
        _platform.Worker.Dispose();
        _copy.Dispose();
    }
}
