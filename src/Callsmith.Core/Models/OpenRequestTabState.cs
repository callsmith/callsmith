namespace Callsmith.Core.Models;

/// <summary>
/// Persisted state for a single open request tab in the request editor.
/// </summary>
public sealed class OpenRequestTabState
{
    /// <summary>
    /// Relative path (from the collection root) of the saved request backing this tab.
    /// Null when the tab has never been saved or the backing file no longer exists.
    /// </summary>
    public string? SourceFilePath { get; init; }

    /// <summary>
    /// Current in-editor request state for tabs that contain unsaved changes.
    /// Null for clean saved tabs, which are restored directly from <see cref="SourceFilePath"/>.
    /// </summary>
    public CollectionRequest? DraftRequest { get; init; }
}
