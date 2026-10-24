namespace Dictation.Services;

/// <summary>
/// Why the microphone will or will not open, as a named state rather than as
/// one failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Every microphone problem on Windows collapsed
/// into <c>MicrophoneTapOutcome.DeviceUnavailable</c> → the pill "The
/// microphone would not open", which is indistinguishable from a broken
/// feature. macOS has enumerated these as distinct states with their own copy
/// since the beginning — <c>DictationAvailability.needsMicrophone</c> and
/// <c>microphoneBusy(owner:)</c>, the second of which says out loud "This is
/// normal, not a fault."
/// </para>
/// <para>
/// Windows 11 ships a per-app microphone switch under Privacy &amp; security.
/// If it is off — or an administrator's policy turned it off — dictation fails
/// with no hint that a permission exists, let alone where. On a managed machine
/// that is the likeliest first-run failure there is.
/// </para>
/// <para>
/// <b><see cref="Unknown"/> is zero deliberately</b>, and it behaves exactly as
/// the build did before this type existed: no extra sentence, no button, the
/// ordinary copy. A probe that cannot answer must never be able to accuse
/// Windows of blocking something.
/// </para>
/// </remarks>
public enum MicrophoneAccess
{
    /// <summary>
    /// Nothing was read, or nothing readable said anything. Treated as "carry
    /// on" everywhere — see the type remarks.
    /// </summary>
    Unknown = 0,

    /// <summary>Windows permits it, and nothing else appears to hold it.</summary>
    Allowed,

    /// <summary>
    /// The user (or a setup wizard, or a previous install) turned microphone
    /// access off for this app or for desktop apps generally. Recoverable by
    /// the person sitting there, which is what makes the deep link worth
    /// offering.
    /// </summary>
    Denied,

    /// <summary>
    /// An administrative policy denies it. Same effect, different advice: the
    /// Settings toggle is greyed out, so sending the user there would be
    /// sending them to a control they cannot move.
    /// </summary>
    DeniedByPolicy,

    /// <summary>
    /// Permitted, but another application currently holds a capture session.
    /// macOS's <c>microphoneBusy(owner:)</c>. Not a fault and not something to
    /// fix — the copy says so.
    /// </summary>
    Busy,
}

/// <summary>
/// Reads whether Windows will let this process open a microphone.
/// </summary>
/// <remarks>
/// A seam, for the same reason <c>ISecureFieldProbe</c> is one: the answer is a
/// registry walk that exists only on Windows, and every rule about what to SAY
/// about the answer belongs in a project that can be tested without one.
/// Implementations must never throw — see <see cref="MicrophoneAccess.Unknown"/>.
/// </remarks>
public interface IMicrophoneAccessProbe
{
    /// <summary>The current state. Cheap enough to call on a settings refresh.</summary>
    MicrophoneAccess Probe();
}

/// <summary>
/// What each <see cref="MicrophoneAccess"/> state says to the user, and whether
/// there is anywhere to send them.
/// </summary>
/// <remarks>
/// Pure and static, in plain <c>net9.0</c>, so every sentence below is
/// assertable on a machine with no Windows in it — the same split
/// <see cref="DictationHudModel"/> and <c>DictationTextFormatter</c> make.
/// </remarks>
public static class MicrophoneAccessCopy
{
    /// <summary>
    /// The Settings page holding the per-app microphone switch.
    /// </summary>
    /// <remarks>
    /// The same <c>ms-settings:</c> URI shape the shell already launches
    /// elsewhere, so there is no new mechanism here — only a page nothing was
    /// pointing at.
    /// </remarks>
    public const string PrivacySettingsUri = "ms-settings:privacy-microphone";

    /// <summary>The label on the button, when there is one.</summary>
    public const string OpenSettingsLabel = "Open microphone settings";

    /// <summary>The label on the re-read button.</summary>
    /// <remarks>
    /// The deep link sends the user out of the app to change something. Without
    /// this, the only way back to a fresh answer is to notice that the sentence
    /// is stale — so the accusation stayed on screen after the user had already
    /// done what it asked. voicebox pairs Open Settings with Recheck for exactly
    /// this reason (<c>AccessibilityGate.tsx</c>).
    /// </remarks>
    public const string RecheckLabel = "Check again";

    /// <summary>
    /// The sentence for a state, or <c>null</c> when there is nothing to add.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MicrophoneAccess.Allowed"/> and
    /// <see cref="MicrophoneAccess.Unknown"/> both return <c>null</c>, and the
    /// surfaces render nothing extra. A card that said "the microphone is fine"
    /// on every ordinary launch would be one more line to read past.
    /// </para>
    /// <para>
    /// The busy sentence carries macOS's own reassurance verbatim. It is there
    /// because the state is otherwise read as a fault, and the user's next move
    /// after "not a fault" is to wait rather than to file a bug.
    /// </para>
    /// </remarks>
    public static string? Sentence(MicrophoneAccess access) => access switch
    {
        MicrophoneAccess.Denied =>
            "Windows is blocking the microphone for this app. Earlet cannot hear anything until "
            + "microphone access is turned on for it in Privacy & security.",

        MicrophoneAccess.DeniedByPolicy =>
            "A policy on this computer blocks microphone access for desktop apps. "
            + "Earlet cannot hear anything until an administrator changes it — the "
            + "switch in Settings will be greyed out.",

        MicrophoneAccess.Busy =>
            "Something else is using the microphone right now. This is normal, not a fault: "
            + "dictation works again as soon as the other application lets go.",

        // Allowed and Unknown. Nothing to say, and saying nothing is the
        // point — see the remarks.
        _ => null,
    };

    /// <summary>
    /// Whether to offer the deep link into Settings for this state.
    /// </summary>
    /// <remarks>
    /// <see cref="MicrophoneAccess.Denied"/> only. Under a policy the toggle is
    /// greyed out, so the button would carry a user to a control they cannot
    /// move — which reads as the app being wrong about its own diagnosis. Busy
    /// has nothing to change.
    /// </remarks>
    public static bool OffersSettingsLink(MicrophoneAccess access) =>
        access == MicrophoneAccess.Denied;

    /// <summary>
    /// Whether to offer the re-read button for this state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wherever there is a sentence — which is a WIDER set than
    /// <see cref="OffersSettingsLink"/>, and deliberately so. Re-reading claims
    /// nothing about who can fix the problem, so the argument that keeps the
    /// deep link away from <see cref="MicrophoneAccess.DeniedByPolicy"/> does
    /// not apply: an administrator may well have changed the policy since the
    /// last read, and the user has no other way to find out.
    /// </para>
    /// <para>
    /// <see cref="MicrophoneAccess.Busy"/> is the state this helps most. The
    /// other application letting go is the single most likely thing to happen
    /// next, and it is invisible until something asks again.
    /// </para>
    /// </remarks>
    public static bool OffersRecheck(MicrophoneAccess access) =>
        Sentence(access) is not null;

    /// <summary>
    /// What to add under the button when a re-read changed nothing, or
    /// <c>null</c> when this state is not worth reporting as unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A button that silently does nothing is worse than no button: the user
    /// cannot tell a working re-read from a dead control. Each state says what
    /// specifically is still true, because "nothing changed" alone leaves them
    /// with no next move.
    /// </para>
    /// <para>
    /// Note that <see cref="MicrophoneAccess.Busy"/> does not say "blocked"
    /// here, for the same reason its own sentence does not: it is not a fault,
    /// and <see cref="IsBlocked"/> agrees.
    /// </para>
    /// </remarks>
    public static string? Unchanged(MicrophoneAccess access) => access switch
    {
        MicrophoneAccess.Denied =>
            "Still blocked. This app's own switch is under Privacy & security, Microphone — "
            + "and the switch above it, letting desktop apps use the microphone at all, "
            + "has to be on as well.",

        MicrophoneAccess.DeniedByPolicy =>
            "Still blocked by policy. Nothing on this computer will change it until an "
            + "administrator does.",

        MicrophoneAccess.Busy =>
            "Still in use by another application. Closing or muting it frees the "
            + "microphone; nothing here needs fixing.",

        // Allowed and Unknown: the whole block has collapsed by now, so there
        // is nothing to report and nowhere to report it.
        _ => null,
    };

    /// <summary>
    /// Whether this state means a hold will produce nothing at all.
    /// </summary>
    /// <remarks>
    /// Busy is deliberately NOT blocked. Windows shares capture endpoints, so
    /// another application holding one usually does not stop this one opening
    /// it — the state is worth naming when a hold fails, and worth not
    /// pre-emptively refusing on.
    /// </remarks>
    public static bool IsBlocked(MicrophoneAccess access) =>
        access is MicrophoneAccess.Denied or MicrophoneAccess.DeniedByPolicy;
}
