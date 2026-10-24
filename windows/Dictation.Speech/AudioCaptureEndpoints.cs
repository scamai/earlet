namespace Dictation.Speech;

/// <summary>
/// One capture endpoint the machine offers.
/// </summary>
/// <param name="Id">
/// The stable endpoint id, as the platform reports it. What gets persisted —
/// never the name, which the user can rename and which two identical headsets
/// share.
/// </param>
/// <param name="Name">The endpoint's friendly name, for the picker's row.</param>
/// <param name="IsSystemDefault">
/// Whether this is the endpoint the system would choose on its own. Carried so
/// the picker can say which one "the system default" currently means, rather
/// than offering a row whose meaning the user has to guess at.
/// </param>
public readonly record struct AudioCaptureEndpoint(string Id, string Name, bool IsSystemDefault);

/// <summary>
/// The capture endpoints a device can open, and which one it will open next.
/// </summary>
/// <remarks>
/// <para>
/// <b>A capability interface rather than three more members on
/// <see cref="IAudioCaptureDevice"/>.</b> That interface is implemented by an
/// in-memory fake in three test projects, none of which has any business
/// growing an endpoint enumeration, and by exactly one real device. A surface
/// that needs the list asks for this and gets <c>null</c> from anything that
/// cannot answer — which is the honest shape: the picker is then absent rather
/// than present and empty.
/// </para>
/// <para>
/// <b>It closes a duplication the tree documented against itself.</b> The
/// dictation pane resolved the microphone's name by constructing its own
/// <c>MMDeviceEnumerator</c> and asking CoreAudio the same question the capture
/// device asks — with its own remark saying so: "this is a duplicated call and
/// it should not stay one... two places asking CoreAudio the same question can
/// answer differently the instant one of them changes its Role, and then the
/// pane names a device the tap is not using."
/// </para>
/// </remarks>
public interface IAudioCaptureEndpointDirectory
{
    /// <summary>
    /// Every capture endpoint currently present, or an empty list when the
    /// machine has none or the enumeration failed. <b>Never throws</b> — a
    /// settings pane must not be able to die on a missing microphone.
    /// </summary>
    IReadOnlyList<AudioCaptureEndpoint> Enumerate();

    /// <summary>
    /// The endpoint the user pinned, or <c>null</c> for "follow the system
    /// default".
    /// </summary>
    /// <remarks>
    /// Null is a real and preferable state, not an unset one. A user who has
    /// never chosen should keep tracking whatever Windows calls the
    /// communications microphone — plugging in a headset then does the right
    /// thing with no visit to this pane at all.
    /// </remarks>
    string? SelectedEndpointId { get; }

    /// <summary>
    /// Pin an endpoint, or pass <c>null</c> to go back to the system default.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the choice was refused — which today means only one
    /// thing: the endpoint is open and a hold is in flight. A device must not
    /// be swapped out from under an utterance, and reporting the refusal is
    /// what lets the caller leave the picker showing what is actually in use.
    /// </returns>
    bool Select(string? endpointId);

    /// <summary>
    /// The name of the endpoint the next <see cref="IAudioCaptureDevice.Start"/>
    /// would open, resolved the same way <c>Start</c> resolves it.
    /// </summary>
    /// <remarks>
    /// A measurement of what a hold would do, not a label. It follows the pin
    /// when there is one and the system default when there is not, and it says
    /// so in words on a machine with no capture endpoint at all — a blank fact
    /// under a label reads as a pane that failed to load.
    /// </remarks>
    string ResolvedEndpointName { get; }
}
