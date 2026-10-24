using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace Dictation.App;

/// <summary>Asks a screen reader to speak a sentence wherever its focus is.</summary>
/// <remarks>
/// A UI Automation notification event (Windows 10 1709 and later), the
/// counterpart of the Mac's <c>announcementRequested</c>, which speaks
/// whatever has the focus: the indicator is never activated, so its live
/// region is heard only if the reader follows live regions in a window that
/// is not in front, which nobody has seen Narrator do. Raised with the live
/// region's event, behind the same gate, never while the microphone is open.
/// One activity for every sentence, most recent first: a newer ending
/// replaces an older one still waiting to be spoken. Never throws, as
/// <see cref="LiveRegion"/>.
/// </remarks>
internal static class UiaNotification
{
    /// <summary>Every sentence the indicator speaks is one activity.</summary>
    public const string Activity = "ScamAI.Dictation.Indicator";

    public static void Raise(UIElement element, string sentence)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.Notification))
        {
            return;
        }

        try
        {
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, sentence, Activity);
        }
        catch (ElementNotAvailableException)
        {
            // The element is being torn down; there is nobody left to tell.
        }
        catch (EntryPointNotFoundException)
        {
            // Windows older than 1709 has no notification event; the live
            // region still speaks there, if anything does.
        }
    }
}
