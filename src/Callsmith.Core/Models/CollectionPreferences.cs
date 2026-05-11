namespace Callsmith.Core.Models;

/// <summary>
/// Personal, per-collection preferences stored in the user's application data directory.
/// These survive across sessions but are never committed to version control.
/// </summary>
public sealed class CollectionPreferences
{
    /// <summary>
    /// Path of the environment file that was active when the collection was last closed,
    /// stored relative to the collection folder. Null means no environment was selected.
    /// </summary>
    public string? LastActiveEnvironmentFile { get; init; }

    /// <summary>
    /// Full persisted state for open tabs in left-to-right display order.
    /// Includes unsaved tab drafts so they can be restored after app restart.
    /// </summary>
    public IReadOnlyList<OpenRequestTabState>? OpenTabs { get; init; }

    /// <summary>
    /// Zero-based index of the active tab within <see cref="OpenTabs"/>.
    /// </summary>
    public int? ActiveTabIndex { get; init; }

    /// <summary>
    /// Relative paths (from the collection folder root) of folders that are expanded
    /// in the sidebar tree. Null means the preference was never saved and the default
    /// (all folders expanded) applies. An empty list means all folders are collapsed.
    /// </summary>
    public IReadOnlyList<string>? ExpandedFolderPaths { get; init; }
}
