using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.CollectionsViewModel"/> when the user opens
/// a collection folder. Carries the absolute path of the opened folder.
/// Received by <see cref="ViewModels.EnvironmentViewModel"/> to refresh the
/// available environments list.
/// </summary>
public sealed class CollectionOpenedMessage(string collectionFolderPath)
    : ValueChangedMessage<string>(collectionFolderPath);
