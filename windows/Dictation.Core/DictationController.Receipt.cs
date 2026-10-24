namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the indicator's
// "Remembered your correction", when the Vocabulary section has just shown a
// learning receipt.
public sealed partial class DictationController
{
    // ─── Learning receipt ───────────────────────────────────────────────

    /// <summary>
    /// The Vocabulary section has just shown a learning receipt
    /// (<see cref="VocabularyModel.CorrectionRemembered"/>): say so on the
    /// indicator, as the Mac does, but only while dictation is on and at rest —
    /// nothing held, no press waiting on its password check, nothing being
    /// transcribed or typed — so learning never interrupts a dictation.
    /// </summary>
    /// <param name="announce">False while the Dictation window is in front:
    /// its receipt line is a live region Narrator reads then, and one
    /// announcement is enough.</param>
    public void ShowCorrectionRemembered(bool announce = true)
    {
        if (!AtRest || _session is null || _fieldCheck is not null)
        {
            return;
        }

        _hud?.CorrectionRemembered(announce);
    }
}
