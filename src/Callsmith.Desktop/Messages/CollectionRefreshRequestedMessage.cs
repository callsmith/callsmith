using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Sent by <see cref="ViewModels.RequestEditorViewModel"/> after a brand-new request tab
/// is saved for the first time (Save As), creating a new file on disk.
/// <see cref="ViewModels.CollectionsViewModel"/> listens for this and refreshes the tree.
/// </summary>
public sealed class CollectionRefreshRequestedMessage() : ValueChangedMessage<int>(0);
