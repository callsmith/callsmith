using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Sent by <see cref="ViewModels.CollectionsViewModel"/> when a collection item is deleted
/// from disk. The value is either the deleted request's file path or a deleted folder's path
/// (with a trailing directory separator). <see cref="ViewModels.RequestEditorViewModel"/> uses this
/// to close any open tab whose request was inside the deleted item.
/// </summary>
public sealed class CollectionItemDeletedMessage(string deletedPath) : ValueChangedMessage<string>(deletedPath);
