using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the "Try
// dictation" practice field — when the window shows it, and the one setup
// value it keeps.
public sealed partial class DictationController
{
    // ─── Try dictation ──────────────────────────────────────────────────

    /// <summary>
    /// What this run last learned about the microphone says Windows blocks
    /// it. Set by every blocked sentence the window is given (the probe at
    /// enable, Check again, a hold that ended blocked); cleared only by a
    /// probe that found access allowed (at enable, or the recheck on window
    /// activation and Check again) or by a dictation that delivered its words,
    /// which needed the microphone open. Unlike the status line it survives
    /// whatever sentence comes next — a language or hold-key change, a hold's
    /// "Listening…" — so none of those can bring the practice field back.
    /// </summary>
    private bool _microphoneBlocked;

    /// <summary>The blocked sentence that last set <see cref="_microphoneBlocked"/>:
    /// whether the block is the user's switch, with the privacy page to offer,
    /// or a policy. Read only while that flag is set.</summary>
    private DictationStatus? _microphoneBlockedBy;

    /// <summary>
    /// The practice field can work: dictation is on and Windows is not
    /// blocking the microphone — Windows' counterpart of the Mac's "every
    /// permission is ready", since the microphone is the only thing Windows
    /// asks about. Up through the practice dictation itself, which runs with
    /// the session armed.
    /// </summary>
    private bool PracticeVisible => _session is not null && !_microphoneBlocked;

    /// <summary>
    /// After a dictation that delivered its words: the microphone evidently
    /// opened, and the first such dictation completes setup, so the practice
    /// field starts collapsed from then on. Only a delivery counts — typing in
    /// the field, a refused paste whose words went to the clipboard, a cancel
    /// or a password field never do — and the value kept is a boolean, never
    /// the text. A save the store refuses (the settings file could not be read
    /// at launch) keeps it for this run and says so in the log, like every
    /// other setting.
    /// </summary>
    private void AfterDelivery(DictationSessionResult result)
    {
        if (!result.TextWasDelivered)
        {
            return;
        }

        _microphoneBlocked = false;
        if (_settings.CompletedFirstDictation)
        {
            return;
        }

        _settings = _settings with { CompletedFirstDictation = true };
        var saved = _settingsStore.Save(_settings);
        _log.LogInformation("First dictation delivered; Try dictation starts collapsed from now on (saved: {Saved})", saved);
    }
}
