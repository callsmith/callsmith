using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.RequestEditorViewModel"/> whenever the active tab
/// changes (tab selected, opened, or closed). The value is the source file path of the
/// newly-active request, or <see cref="string.Empty"/> when there is no active request
/// (e.g. a new unsaved tab, or all tabs are closed).
/// Received by <see cref="ViewModels.CollectionsViewModel"/> to highlight the matching
/// tree node.
/// </summary>
public sealed class ActiveTabChangedMessage(string filePath)
    : ValueChangedMessage<string>(filePath);
