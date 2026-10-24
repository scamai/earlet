namespace Dictation.Speech;

/// <summary>How the microphone an open capture records from stopped being the
/// one a hold would use.</summary>
/// <remarks>
/// One kind, the Mac's: the in-hold watchdog (D15) ends a hold when the device
/// it bound at the press goes away, never because the system default moved.
/// A default that moves mid-hold (a headset plugged in) leaves the hold to
/// finish on the microphone it opened, and the next hold opens the new one.
/// </remarks>
public enum AudioCaptureEndpointChangeKind
{
    /// <summary>The endpoint the capture has open was unplugged, disabled or
    /// removed: no more audio will come from it.</summary>
    Removed,
}

/// <summary>
/// The microphone changed while a capture was open.
/// </summary>
/// <param name="Kind">What changed.</param>
/// <param name="NewEndpointName">The friendly name of the endpoint the next
/// open would use (whatever Windows falls back to from the removed one), as
/// Windows shows it in Sound settings; null when there is none or it could
/// not be read. Display text only: never an id, never logged by the caller.</param>
/// <param name="Capture">Which open of the endpoint this change was seen
/// during (<see cref="IAudioCaptureEndpointWatch.Capture"/>), so a change
/// that reaches its listener after that capture closed and another opened is
/// recognised as stale.</param>
public sealed record AudioCaptureEndpointChange(
    AudioCaptureEndpointChangeKind Kind,
    string? NewEndpointName,
    int Capture);

/// <summary>
/// A capture device that says when the microphone it has open stops being
/// the one to record from, mid-capture.
/// </summary>
/// <remarks>
/// <para>
/// A capability interface, as <see cref="IAudioCaptureEndpointDirectory"/> is:
/// the fakes in other test projects need not grow it, and a listener asks for
/// it with a type test and does without when a device cannot answer.
/// </para>
/// <para>
/// What the Dictation app ends a take on, keeping it: what was captured is
/// transcribed and inserted, with "The microphone stopped at m:ss" (since
/// LIMIT 2026-10-10; the Mac's "Mic changed to … — could you say that
/// again?" discards). It is the Mac's in-hold watchdog (D15) trigger for a
/// bound device gone from the roster. Like the Mac, a default that moves
/// while the open microphone still delivers concerns nothing here. A stream
/// that stalls is the app's in-hold watchdog's (<c>DictationCaptureWatchdog</c>),
/// and one Windows stops with an error says so through
/// <see cref="IAudioCaptureFaultWatch"/>; neither is this interface's.
/// Windows reports device changes as they happen
/// (<c>IMMNotificationClient</c>), so nothing here polls; what is decided
/// here is only which notifications concern the open capture, and which
/// microphone a sentence may name (<see cref="AudioCaptureEndpointChangeRule"/>).
/// </para>
/// </remarks>
public interface IAudioCaptureEndpointWatch
{
    /// <summary>
    /// The open capture's endpoint was removed. Raised at most once per
    /// capture, only while one is open, on a thread-pool thread: never on the
    /// audio system's notification thread, and never on the caller's.
    /// </summary>
    event EventHandler<AudioCaptureEndpointChange>? EndpointChanged;

    /// <summary>How many captures this device has opened, counting from 1;
    /// 0 before the first. A change names the capture it was seen during.</summary>
    int Capture { get; }
}

/// <summary>
/// Which endpoint notifications concern an open capture: the pure half of
/// <see cref="IAudioCaptureEndpointWatch"/>, so the rule is checked on a
/// machine with no audio stack. Only the open endpoint going away does; there
/// is deliberately no rule for a default that moves, as the Mac's watchdog
/// has none. Endpoint ids are compared as opaque strings, ignoring case.
/// </summary>
public static class AudioCaptureEndpointChangeRule
{
    /// <summary>An endpoint's state changed: it concerns the capture when it
    /// is the open endpoint and it is no longer active (disabled, not present
    /// or unplugged).</summary>
    /// <param name="openEndpointId">The endpoint the capture opened, or null.</param>
    /// <param name="endpointId">The endpoint whose state changed.</param>
    /// <param name="stillActive">Its new state is active.</param>
    public static AudioCaptureEndpointChangeKind? StateChanged(string? openEndpointId, string? endpointId, bool stillActive) =>
        openEndpointId is not null && !stillActive && Same(openEndpointId, endpointId)
            ? AudioCaptureEndpointChangeKind.Removed
            : null;

    /// <summary>An endpoint was removed: it concerns the capture when it is
    /// the open endpoint.</summary>
    /// <param name="openEndpointId">The endpoint the capture opened, or null.</param>
    /// <param name="endpointId">The endpoint that was removed.</param>
    public static AudioCaptureEndpointChangeKind? Removed(string? openEndpointId, string? endpointId) =>
        openEndpointId is not null && Same(openEndpointId, endpointId)
            ? AudioCaptureEndpointChangeKind.Removed
            : null;

    /// <summary>
    /// The name a sentence may give the microphone the next hold would open,
    /// as Windows resolved it just after the change: null when Windows
    /// resolved none, when it resolved the very endpoint the capture had open
    /// (a removal can be reported before Windows moves its default off the
    /// headset just unplugged), when that endpoint is not active, or when it
    /// has no name to show. The sentence then names no microphone, as the
    /// Mac's names none when its rebind landed nowhere, rather than a wrong one.
    /// </summary>
    /// <param name="openEndpointId">The endpoint the capture had open.</param>
    /// <param name="nextEndpointId">The endpoint Windows resolved, or null.</param>
    /// <param name="nextActive">That endpoint's state is active.</param>
    /// <param name="nextName">That endpoint's friendly name, or null.</param>
    public static string? NameOfNext(string? openEndpointId, string? nextEndpointId, bool nextActive, string? nextName) =>
        nextEndpointId is null
        || !nextActive
        || Same(openEndpointId, nextEndpointId)
        || string.IsNullOrWhiteSpace(nextName)
            ? null
            : nextName;

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
