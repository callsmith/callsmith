namespace Callsmith.Core.Models;

/// <summary>
/// A single recorded request/response cycle stored in request history.
/// <para>
/// Each entry carries two complementary views of the request:
/// <list type="bullet">
///   <item>
///     <term>Configured snapshot</term>
///     <description>
///       The complete request configuration at the moment Send was pressed —
///       user-authored fields from <see cref="CollectionRequest"/> plus any headers
///       the application applied automatically (e.g. <c>Content-Type</c>).
///       Variable placeholders (<c>{{token}}</c>) are preserved in their unresolved form.
///       Captured at send time, never re-read from disk, so it is self-contained even
///       when the source file is later renamed, edited, or deleted.
///     </description>
///   </item>
///   <item>
///     <term>Variable bindings</term>
///     <description>
///       A comprehensive map of every <c>{{token}}</c> → resolved-value substitution
///       that occurred, including authentication field resolutions.  Together with the
///       configured snapshot this lets the UI reconstruct the exact wire-level
///       ("Resolved") view without re-running variable resolution.
///     </description>
///   </item>
///   <item>
///     <term>Response snapshot</term>
///     <description>The complete server response as received.</description>
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed record HistoryEntry
{
    // -------------------------------------------------------------------------
    // Identity / indexing fields (stored as dedicated columns for query performance)
    // -------------------------------------------------------------------------

    /// <summary>Surrogate primary key assigned by the repository on write.</summary>
    public long Id { get; init; }

    /// <summary>
    /// Stable identifier of the saved request this entry originated from.
    /// <see langword="null"/> for ad-hoc requests sent from unsaved tabs.
    /// </summary>
    public Guid? RequestId { get; init; }

    /// <summary>UTC instant when the request was sent.</summary>
    public DateTimeOffset SentAt { get; init; }

    /// <summary>HTTP status code of the response (e.g. 200, 404). Null if no response was received.</summary>
    public int? StatusCode { get; init; }

    /// <summary>HTTP method as a string (GET, POST, …).</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>
    /// The fully-resolved URL that was sent — with all <c>{{tokens}}</c> substituted,
    /// path parameters applied, and query parameters URL-encoded.
    /// Pre-computed by <see cref="Services.HistorySentViewBuilder"/> at capture time for indexing.
    /// </summary>
    public string ResolvedUrl { get; init; } = string.Empty;

    /// <summary>Display name of the request as it was saved, if it was a saved request.</summary>
    public string? RequestName { get; init; }

    /// <summary>Display name of the collection the request belongs to, if applicable.</summary>
    public string? CollectionName { get; init; }

    /// <summary>
    /// Display name of the selected environment at send time.
    /// Null when the request was sent without a concrete environment selected.
    /// </summary>
    public string? EnvironmentName { get; init; }

    /// <summary>
    /// Stable identifier of the selected environment at send time.
    /// Null when the request was sent without a concrete environment selected.
    /// </summary>
    public Guid? EnvironmentId { get; init; }

    /// <summary>
    /// Optional display color of the selected environment at send time (hex string).
    /// Stored so history UI can keep showing the original swatch even if the environment
    /// is later renamed or deleted.
    /// </summary>
    public string? EnvironmentColor { get; init; }

    /// <summary>Root folder path of the collection, for linking back to the source.</summary>
    public string? CollectionPath { get; init; }

    /// <summary>Total elapsed time from send to full response in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    // -------------------------------------------------------------------------
    // Configured snapshot
    // -------------------------------------------------------------------------

    /// <summary>
    /// The request configuration exactly as it was at the moment Send was pressed.
    /// Includes the user's authored fields and any app-applied headers (e.g. Content-Type).
    /// Variable placeholders are preserved in their unresolved <c>{{token}}</c> form.
    /// </summary>
    public required ConfiguredRequestSnapshot ConfiguredSnapshot { get; init; }

    // -------------------------------------------------------------------------
    // Variable bindings
    // -------------------------------------------------------------------------

    /// <summary>
    /// Every variable substitution that occurred during request preparation.
    /// Covers URL, path params, query params, headers, body, form params, and auth fields.
    /// Entries with <see cref="VariableBinding.IsSecret"/> = <see langword="true"/> are stored
    /// encrypted by the repository and decrypted on demand.
    /// </summary>
    public IReadOnlyList<VariableBinding> VariableBindings { get; init; } = [];

    // -------------------------------------------------------------------------
    // Response snapshot
    // -------------------------------------------------------------------------

    /// <summary>The server response as received. Null if the request failed before a response arrived.</summary>
    public ResponseSnapshot? ResponseSnapshot { get; init; }
}
