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

    /// <summary>
    /// Pixel width of the left sidebar (request tree) column.
    /// Null means the default width is used.
    /// </summary>
    public double? RequestTreeSplitterPosition { get; init; }

    /// <summary>
    /// Pixel size of the first panel in the history detail view.
    /// When <see cref="IsHorizontalHistoryDetailLayout"/> is true this is the left panel width (x).
    /// When false it is the top panel height (y).
    /// Null means the default ratio is used.
    /// </summary>
    public double? HistoryDetailSplitterPosition { get; init; }

    /// <summary>
    /// Pixel size of the first panel in the request editor view.
    /// When <see cref="IsHorizontalRequestEditorLayout"/> is true this is the left panel width (x).
    /// When false it is the top panel height (y).
    /// Null means the default ratio is used.
    /// </summary>
    public double? RequestEditorSplitterPosition { get; init; }
}
