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
    /// Pixel width of the left (request) panel when the history detail view is in horizontal layout.
    /// Null means the default ratio is used.
    /// </summary>
    public double? HistoryDetailHorizontalSplitterPosition { get; init; }

    /// <summary>
    /// Pixel height of the top (request) panel when the history detail view is in vertical layout.
    /// Null means the default ratio is used.
    /// </summary>
    public double? HistoryDetailVerticalSplitterPosition { get; init; }

    /// <summary>
    /// Pixel width of the left (request config) panel when the request editor is in horizontal layout.
    /// Null means the default ratio is used.
    /// </summary>
    public double? RequestEditorHorizontalSplitterPosition { get; init; }

    /// <summary>
    /// Pixel height of the top (request config) panel when the request editor is in vertical layout.
    /// Null means the default ratio is used.
    /// </summary>
    public double? RequestEditorVerticalSplitterPosition { get; init; }

    /// <summary>
    /// Pixel width of the history-list panel (left side of the history screen).
    /// Null means the default 320 px fixed width is used.
    /// </summary>
    public double? HistoryListSplitterPosition { get; init; }
}
