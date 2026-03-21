using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Sent by <see cref="ViewModels.CollectionsViewModel"/> when a request is successfully
/// renamed via the rename dialog. Carries the old file path and the updated
/// <see cref="CollectionRequest"/> with the new name and file path.
/// Open request tabs listen for this to update their in-memory state, including
/// the displayed name and file path for session persistence and cache invalidation.
/// </summary>
public sealed class RequestRenamedMessage(string oldFilePath, CollectionRequest renamed)
{
    /// <summary>The original file path before renaming.</summary>
    public string OldFilePath { get; } = oldFilePath;

    /// <summary>The updated request with new name and file path.</summary>
    public CollectionRequest Renamed { get; } = renamed;
}
