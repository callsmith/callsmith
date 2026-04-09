using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published when the user selects a request — either from the sidebar tree or the
/// command palette. Received by <see cref="ViewModels.RequestEditorViewModel"/> to
/// populate or open a tab.
/// </summary>
/// <param name="request">The request to open.</param>
/// <param name="openAsPermanent">
/// When <see langword="true"/> the tab is opened as a permanent (non-transient) tab
/// and the existing transient tab is not replaced. Used when opening from the command
/// palette, where the user has intentionally chosen a request to work with.
/// </param>
public sealed class RequestSelectedMessage(CollectionRequest request, bool openAsPermanent = false)
    : ValueChangedMessage<CollectionRequest>(request)
{
    /// <summary>
    /// When <see langword="true"/> the editor should open the request as a permanent tab,
    /// bypassing the transient-tab behavior used for sidebar single-clicks.
    /// </summary>
    public bool OpenAsPermanent { get; } = openAsPermanent;
}
