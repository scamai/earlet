using Dictation.Services;

namespace Dictation.Core.Tests;

/// <summary>
/// A paste the target refused leaves the words on the clipboard, as the macOS
/// app does, and says so — never for a password field, and never with the
/// words in the log.
/// </summary>
public sealed class DictationClipboardFallbackTests
{
    [Fact]
    public async Task A_refused_paste_leaves_the_text_on_the_clipboard_and_says_so()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Paste.Succeeds = false;

        rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboard);

        Assert.Equal(
            "Couldn't type into that app. The text is on the clipboard — paste it with Ctrl+V.",
            rig.View.Last.Status);
        Assert.Equal("hello world", rig.Platform.Clipboard.Text, ignoreCase: true);
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task An_unidentified_window_is_refused_and_the_text_is_kept()
    {
        // An elevated editor: the paste is refused before it is sent.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Target.Next = FocusedApplicationResult.Unresolvable("Administrator: Editor");

        rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboard);

        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Equal("hello world", rig.Platform.Clipboard.Text, ignoreCase: true);
    }

    [Fact]
    public async Task With_no_window_to_type_into_the_text_is_kept()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Target.Next = FocusedApplicationResult.NoFocusedWindow;

        rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboardNoWindow);

        Assert.Equal("hello world", rig.Platform.Clipboard.Text, ignoreCase: true);
    }

    /// <summary>
    /// The clipboard would not take the words either: they are not lost, and
    /// not claimed to be on the clipboard. They stay in memory for Copy last
    /// text, and the sentence says where (RecentResultTests has the rest).
    /// </summary>
    [Fact]
    public async Task A_clipboard_that_refuses_the_text_leaves_the_words_in_Dictation_and_says_where()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Clipboard.Accepts = false;

        rig.Dictate(v => v.Status == DictationCopy.WordsStillHere);

        Assert.DoesNotContain(rig.View.States, s => s.Status == DictationCopy.KeptOnClipboard);
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
        Assert.Equal("Hello world", rig.View.Last.Recent?.Text);
        Assert.Contains("Copy last text", rig.View.Last.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_field_never_leaves_words_on_the_clipboard()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Platform.SecureField.Next = SecureFieldState.Secure;
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtRelease);

        Assert.Empty(rig.Platform.Clipboard.Writes);
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
    }

    [Fact]
    public async Task A_delivered_dictation_puts_the_user_s_clipboard_back()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        rig.Dictate(v => v.Status.StartsWith("Text inserted", StringComparison.Ordinal));

        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
    }

    [Fact]
    public async Task The_log_counts_the_kept_text_and_never_holds_it()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        using var rig = new Rig(logs: logs);
        rig.Platform.Worker.Transcript = "my bank pin is four four two one";
        await rig.EnableAsync();
        rig.Platform.Paste.Succeeds = false;

        rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboard);

        var log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        Assert.Contains("characters were left on the clipboard", log, StringComparison.Ordinal);
        Assert.DoesNotContain("bank pin", log, StringComparison.OrdinalIgnoreCase);
    }
}
