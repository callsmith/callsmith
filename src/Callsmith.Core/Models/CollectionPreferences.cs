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
}
