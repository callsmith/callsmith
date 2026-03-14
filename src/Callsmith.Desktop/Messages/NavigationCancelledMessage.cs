using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Sent by <see cref="ViewModels.RequestViewModel"/> when the user dismisses the
/// unsaved-changes guard and chooses to stay on the current request.
/// <see cref="ViewModels.CollectionsViewModel"/> uses this to revert the sidebar
/// selection back to the previously-open item.
/// </summary>
public sealed class NavigationCancelledMessage : ValueChangedMessage<int>
{
    public NavigationCancelledMessage() : base(0) { }
}
