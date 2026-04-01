namespace Callsmith.Core.Models;

/// <summary>
/// Global application-level preferences stored in the user's application data directory.
/// Unlike <see cref="CollectionPreferences"/>, these are not scoped to a collection.
/// </summary>
public sealed record AppPreferences
{
    /// <summary>
    /// Whether the history detail pane is displayed in horizontal (side-by-side) mode.
    /// True means the request panel is on the left and the response panel is on the right.
    /// False means the request panel is on top and the response panel is below.
    /// Defaults to true (horizontal) when the preference has never been saved.
    /// </summary>
    public bool IsHorizontalHistoryDetailLayout { get; init; } = true;

    /// <summary>
    /// Whether the request editor is displayed in horizontal (side-by-side) mode.
    /// True means the request config panel is on the left and the response panel is on the right.
    /// False means the request config panel is on top and the response panel is below.
    /// Defaults to true (horizontal) when the preference has never been saved.
    /// </summary>
    public bool IsHorizontalRequestEditorLayout { get; init; } = true;
}
