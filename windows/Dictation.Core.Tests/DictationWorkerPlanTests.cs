using System.Text.Json;

namespace Dictation.Core.Tests;

public sealed class DictationWorkerPlanTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Without_overrides_the_plan_is_the_bundled_resources()
    {
        using var copy = new InstalledCopy();

        var plan = DictationWorkerPlan.Resolve(copy.Root, Env());

        Assert.Equal(Path.Combine(copy.Root, "resources", "python", "python.exe"), plan.PythonExecutable);
        Assert.Equal(Path.Combine(copy.Root, "resources", "runtime"), plan.RuntimeDirectory);
        Assert.Equal(copy.ModelDirectory, plan.ModelDirectory);
        Assert.Empty(plan.MissingFiles());
    }

    [Fact]
    public void A_relative_override_is_resolved_against_the_app_directory_not_the_current_one()
    {
        // The check used to run against the current directory while the
        // worker loaded the same string from the runtime directory.
        using var copy = new InstalledCopy();
        var elsewhere = Directory.CreateTempSubdirectory("dictation-cwd-").FullName;
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = elsewhere;

            var plan = DictationWorkerPlan.Resolve(copy.Root, Env(
                (DictationEnvironment.ModelDirectory, Path.Combine("resources", "model")),
                (DictationEnvironment.Runtime, Path.Combine("resources", "runtime"))));

            Assert.True(Path.IsPathFullyQualified(plan.ModelDirectory));
            Assert.Equal(copy.ModelDirectory, plan.ModelDirectory);
            Assert.Equal(Path.Combine(copy.Root, "resources", "runtime"), plan.RuntimeDirectory);
            Assert.Empty(plan.MissingFiles());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(elsewhere);
        }
    }

    [Fact]
    public void A_blank_override_means_the_bundled_location()
    {
        using var copy = new InstalledCopy();

        var plan = DictationWorkerPlan.Resolve(copy.Root, Env((DictationEnvironment.ModelDirectory, "  ")));

        Assert.Equal(copy.ModelDirectory, plan.ModelDirectory);
    }

    [Theory]
    [InlineData("model.bin")]
    [InlineData("config.json")]
    [InlineData("tokenizer.json")]
    [InlineData("preprocessor_config.json")]
    public void A_model_directory_missing_any_of_the_four_files_is_incomplete(string file)
    {
        using var copy = new InstalledCopy();
        copy.Delete(Path.Combine("resources", "model", file));

        var missing = DictationWorkerPlan.Resolve(copy.Root, Env()).MissingFiles();

        Assert.Equal([Path.Combine(copy.ModelDirectory, file)], missing);
    }

    [Fact]
    public void The_required_files_are_the_ones_the_packager_requires()
    {
        // tools/package.py refuses to package a folder without these; a host
        // check that asked for fewer would start a worker on a model the
        // packager calls incomplete.
        var package = File.ReadAllText(Path.Combine(DictationRoot(), "tools", "package.py"));
        var line = package.Split('\n').Single(l => l.StartsWith("WINDOWS_MODEL_FILES = ", StringComparison.Ordinal));
        var named = line[(line.IndexOf('(') + 1)..line.LastIndexOf(')')]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Trim('"'))
            .ToArray();

        Assert.Equal(named, DictationWorkerPlan.RequiredModelFiles);
    }

    private static string DictationRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "tools", "package.py")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("apps/dictation not found above the test output");
    }

    [Fact]
    public async Task Enabling_with_an_incomplete_model_never_starts_the_worker()
    {
        using var rig = new Rig();
        rig.Copy.Delete(Path.Combine("resources", "model", "tokenizer.json"));

        await rig.EnableAsync();

        Assert.Empty(rig.Platform.Plans);
        Assert.Empty(rig.Platform.Worker.Frames);
        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(
            "Speech resources are missing. Install the complete Earlet package and try again.",
            rig.View.Last.Status);
    }

    [Fact]
    public async Task The_worker_is_told_the_absolute_model_directory_a_relative_override_names()
    {
        using var rig = new Rig(environment: name =>
            name == DictationEnvironment.ModelDirectory ? Path.Combine("resources", "model") : null);

        await rig.EnableAsync();

        var init = JsonDocument.Parse(rig.Platform.Worker.Frames.First()).RootElement;
        Assert.Equal("init", init.GetProperty("op").GetString());
        Assert.Equal(rig.Copy.ModelDirectory, init.GetProperty("model_dir").GetString());
        Assert.Equal(rig.Copy.ModelDirectory, init.GetProperty("hf_id").GetString());
    }

    [Fact]
    public async Task The_init_frame_the_host_sends_forbids_model_downloads()
    {
        // Read off the wire, not off the options record: this is the frame the
        // worker's op_init receives, and asr_server.py treats an absent key as
        // "downloads allowed", which skips its offline model check.
        using var rig = new Rig();

        await rig.EnableAsync();

        var init = JsonDocument.Parse(rig.Platform.Worker.Frames.First()).RootElement;
        Assert.Equal("init", init.GetProperty("op").GetString());
        Assert.Equal(JsonValueKind.False, init.GetProperty("allow_model_download").ValueKind);
        Assert.Equal("mac", init.GetProperty("backend").GetString());
        Assert.False(init.GetProperty("use_qnn").GetBoolean());
    }

    [Fact]
    public void A_caller_that_never_sets_the_flag_sends_no_key()
    {
        // The frame other callers of the shared host build is unchanged: the
        // key is omitted, not sent as null or true.
        var json = JsonSerializer.Serialize(
            new Dictation.Speech.AsrInitRequest { Backend = "mac" },
            Dictation.Speech.AsrFraming.JsonOptions);

        Assert.DoesNotContain("allow_model_download", json, StringComparison.Ordinal);
    }
}
