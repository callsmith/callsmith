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
    /// Fraction (0.0–1.0) of the total width occupied by the left sidebar (request tree) column.
    /// Null means the default width is used.
    /// </summary>
    public double? RequestTreeSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the available width occupied by the left (request) panel when
    /// the history detail view is in horizontal layout.
    /// Null means the default 0.45 ratio is used.
    /// </summary>
    public double? HistoryDetailHorizontalSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the available height occupied by the top (request) panel when
    /// the history detail view is in vertical layout.
    /// Null means the default 0.45 ratio is used.
    /// </summary>
    public double? HistoryDetailVerticalSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the available width occupied by the left (request config) panel
    /// when the request editor is in horizontal layout.
    /// Null means the default 0.45 ratio is used.
    /// </summary>
    public double? RequestEditorHorizontalSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the available height occupied by the top (request config) panel
    /// when the request editor is in vertical layout.
    /// Null means the default 0.45 ratio is used.
    /// </summary>
    public double? RequestEditorVerticalSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the total width occupied by the history-list panel
    /// (left side of the history screen).
    /// Null means the default ratio is used.
    /// </summary>
    public double? HistoryListSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the key column width in the headers key/value editor.
    /// Null means the default 0.5 ratio is used.
    /// </summary>
    public double? HeadersKvpSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the key column width in the path params key/value editor.
    /// Null means the default 0.5 ratio is used.
    /// </summary>
    public double? PathParamsKvpSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the key column width in the query params key/value editor.
    /// Null means the default 0.5 ratio is used.
    /// </summary>
    public double? QueryParamsKvpSplitterFraction { get; init; }

    /// <summary>
    /// Fraction (0.0–1.0) of the key column width in the form body key/value editor.
    /// Null means the default 0.5 ratio is used.
    /// </summary>
    public double? FormParamsKvpSplitterFraction { get; init; }
}
