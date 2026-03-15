using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Sent by a <see cref="ViewModels.RequestTabViewModel"/> when an existing request is
/// successfully saved to disk. The value is the updated <see cref="CollectionRequest"/>.
/// <see cref="ViewModels.CollectionsViewModel"/> listens for this and refreshes the
/// matching tree node's in-memory snapshot so it stays in sync with disk.
/// </summary>
public sealed class RequestSavedMessage(CollectionRequest updated)
    : ValueChangedMessage<CollectionRequest>(updated);
