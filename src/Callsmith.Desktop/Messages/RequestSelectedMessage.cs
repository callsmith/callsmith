using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.CollectionsViewModel"/> when the user selects
/// a request in the sidebar tree.
/// Received by <see cref="ViewModels.RequestEditorViewModel"/> to populate or open a tab.
/// </summary>
public sealed class RequestSelectedMessage(CollectionRequest request)
    : ValueChangedMessage<CollectionRequest>(request);
