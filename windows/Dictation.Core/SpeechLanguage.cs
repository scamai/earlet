namespace Dictation.Core;

/// <summary>One choice in the language picker.</summary>
/// <param name="Code">
/// The Whisper language code sent with every transcription, or null for
/// automatic detection.
/// </param>
/// <param name="Name">What the picker shows: the English name, and the
/// language's own name where it differs, so a reader of either finds it.</param>
public sealed record SpeechLanguage(string? Code, string Name);

/// <summary>
/// The languages the Windows app offers. Automatic is the default and the
/// fallback for anything unrecognised.
/// </summary>
/// <remarks>
/// <para>
/// Choosing one is a real trade, which is why the default is Automatic:
/// Whisper does not fail on a mismatch, it translates. Speech in another
/// language, forced through a fixed one, comes out as fluent text in the
/// chosen language that the user never said (see the worker's note on the
/// <c>language</c> field in <c>asr_server.py</c>). A fixed language is
/// steadier on short holds for someone who only ever speaks it.
/// </para>
/// <para>
/// The codes are Whisper's own; the worker forwards the value to the engine
/// unchanged.
/// </para>
/// </remarks>
public static class SpeechLanguages
{
    /// <summary>Let the recogniser work out the language from what it hears.</summary>
    public static SpeechLanguage Automatic { get; } = new(null, "Automatic");

    /// <summary>The picker, in order.</summary>
    public static IReadOnlyList<SpeechLanguage> Choices { get; } =
    [
        Automatic,
        new("en", "English"),
        new("zh", "Chinese (中文)"),
        new("ja", "Japanese (日本語)"),
        new("ko", "Korean (한국어)"),
        new("es", "Spanish (Español)"),
        new("fr", "French (Français)"),
        new("de", "German (Deutsch)"),
        new("pt", "Portuguese (Português)"),
        new("it", "Italian (Italiano)"),
        new("ru", "Russian (Русский)"),
    ];

    /// <summary>Whether <paramref name="code"/> names a choice (null is Automatic).</summary>
    public static bool IsOffered(string? code) =>
        string.IsNullOrWhiteSpace(code) || Choices.Any(c => Matches(c, code));

    /// <summary>The choice for <paramref name="code"/>, or Automatic.</summary>
    public static SpeechLanguage Find(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? Automatic
            : Choices.FirstOrDefault(c => Matches(c, code)) ?? Automatic;

    private static bool Matches(SpeechLanguage choice, string code) =>
        choice.Code is not null && string.Equals(choice.Code, code.Trim(), StringComparison.OrdinalIgnoreCase);
}
