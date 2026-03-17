using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.EnvironmentEditorViewModel"/> after the user
/// reorders environments via drag-and-drop and the new order has been persisted.
/// Received by <see cref="ViewModels.EnvironmentViewModel"/> to re-apply the custom
/// order in the environment dropdown.
/// The message value is the collection folder path that was affected.
/// </summary>
public sealed class EnvironmentOrderChangedMessage(string collectionFolderPath)
    : ValueChangedMessage<string>(collectionFolderPath);
