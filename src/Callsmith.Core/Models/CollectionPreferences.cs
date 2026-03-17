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
    /// Ordered list of environment file basenames (e.g. <c>local.env.callsmith</c>) in
    /// the user's preferred display order. Null or empty means use the default
    /// (alphabetical) ordering. Entries that no longer map to an existing file are
    /// silently ignored; newly-created files not yet in the list appear at the end.
    /// </summary>
    public IReadOnlyList<string>? EnvironmentOrder { get; init; }
}
