using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.EnvironmentEditorViewModel"/> when the user clicks
/// "Back to Editor". Received by <see cref="ViewModels.EnvironmentViewModel"/> to close
/// the editor panel and return to the request view.
/// </summary>
public sealed class CloseEnvironmentEditorMessage : ValueChangedMessage<bool>
{
    public CloseEnvironmentEditorMessage() : base(true) { }
}
