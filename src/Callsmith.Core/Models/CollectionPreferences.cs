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
    /// Relative paths (from the collection folder root) of the open request tabs in
    /// their left-to-right display order. Only saved tabs are included; unsaved
    /// "New Request" tabs are not persisted. Null or empty means no tabs were open.
    /// </summary>
    public IReadOnlyList<string>? OpenTabPaths { get; init; }

    /// <summary>
    /// Relative path of the tab that was active when the collection was last closed.
    /// Null when there was no active saved tab.
    /// </summary>
    public string? ActiveTabPath { get; init; }

    /// <summary>
    /// Relative paths (from the collection folder root) of folders that are expanded
    /// in the sidebar tree. Null means the preference was never saved and the default
    /// (all folders expanded) applies. An empty list means all folders are collapsed.
    /// </summary>
    public IReadOnlyList<string>? ExpandedFolderPaths { get; init; }

    /// <summary>
    /// Whether the request viewer is displayed in horizontal (side-by-side) mode.
    /// True means request config on the left and response on the right.
    /// False means request config on top and response below.
    /// Defaults to true (horizontal) when the preference has never been saved.
    /// </summary>
    public bool IsHorizontalLayout { get; init; } = true;
}
