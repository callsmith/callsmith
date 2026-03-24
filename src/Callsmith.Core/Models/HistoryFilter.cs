namespace Callsmith.Core.Models;

/// <summary>
/// Encapsulates the filter criteria for querying request history.
/// All non-null / non-default fields are combined with AND logic.
/// </summary>
public sealed class HistoryFilter
{
    // -------------------------------------------------------------------------
    // Paging
    // -------------------------------------------------------------------------

    /// <summary>Zero-based page index. Defaults to 0.</summary>
    public int Page { get; init; } = 0;

    /// <summary>Number of results per page. Defaults to 50.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Sort order. <see langword="true"/> for newest first (default),
    /// <see langword="false"/> for oldest first.
    /// </summary>
    public bool NewestFirst { get; init; } = true;

    // -------------------------------------------------------------------------
    // Status code
    // -------------------------------------------------------------------------

    /// <summary>
    /// Minimum status code (inclusive). When set, only entries where
    /// <c>StatusCode &gt;= MinStatusCode</c> are returned.
    /// </summary>
    public int? MinStatusCode { get; init; }

    /// <summary>
    /// Maximum status code (inclusive). When set, only entries where
    /// <c>StatusCode &lt;= MaxStatusCode</c> are returned.
    /// </summary>
    public int? MaxStatusCode { get; init; }

    // -------------------------------------------------------------------------
    // Date range
    // -------------------------------------------------------------------------

    /// <summary>Include only entries sent at or after this instant.</summary>
    public DateTimeOffset? SentAfter { get; init; }

    /// <summary>Include only entries sent at or before this instant.</summary>
    public DateTimeOffset? SentBefore { get; init; }

    // -------------------------------------------------------------------------
    // URL / name free-text
    // -------------------------------------------------------------------------

    /// <summary>
    /// Free-text search over the resolved URL, request name, and collection name fields.
    /// Case-insensitive. Null or empty means no text filter.
    /// </summary>
    public string? TextSearch { get; init; }

    /// <summary>
    /// Global free-text search applied as OR across <em>both</em> the request search text
    /// (body, headers, params, URL, name) and the response search text (headers, body).
    /// Case-insensitive. Takes precedence over — and is independent from — the more
    /// specific <see cref="RequestContains"/> and <see cref="ResponseContains"/> filters.
    /// </summary>
    public string? GlobalSearch { get; init; }

    /// <summary>
    /// Case-insensitive substring search over persisted request search text.
    /// This includes the configured request URL, headers, body, form fields,
    /// query/path parameters, and non-secret auth metadata such as API key header names.
    /// </summary>
    public string? RequestContains { get; init; }

    /// <summary>
    /// Case-insensitive substring search over persisted response search text.
    /// This includes response headers and response body.
    /// </summary>
    public string? ResponseContains { get; init; }

    /// <summary>Controls how <see cref="UrlPattern"/> is matched against the resolved URL.</summary>
    public UrlMatchMode UrlMatch { get; init; } = UrlMatchMode.Contains;

    /// <summary>
    /// URL-specific pattern. Applied using the mode specified by <see cref="UrlMatch"/>.
    /// Null means no URL filter.
    /// </summary>
    public string? UrlPattern { get; init; }

    // -------------------------------------------------------------------------
    // Request / collection name
    // -------------------------------------------------------------------------

    /// <summary>Filter by request name (case-insensitive contains).</summary>
    public string? RequestName { get; init; }

    /// <summary>Filter by collection name (case-insensitive contains).</summary>
    public string? CollectionName { get; init; }

    /// <summary>
    /// Filter by selected environment name used when the request was sent.
    /// Case-insensitive contains.
    /// </summary>
    public string? EnvironmentName { get; init; }

    /// <summary>
    /// Filter by selected environment id used when the request was sent.
    /// When set, this takes precedence over <see cref="EnvironmentName"/>.
    /// </summary>
    public Guid? EnvironmentId { get; init; }

    /// <summary>
    /// When <see langword="true"/>, restricts results to entries sent without any environment
    /// selected (both <see cref="EnvironmentId"/> and environment name are absent).
    /// Takes precedence over <see cref="EnvironmentName"/> and <see cref="EnvironmentId"/>.
    /// </summary>
    public bool NoEnvironment { get; init; }

    // -------------------------------------------------------------------------
    // Header search
    // -------------------------------------------------------------------------

    /// <summary>
    /// Filter by a request header name and/or value.
    /// Matching is performed against the <em>resolved</em> (wire-level) headers.
    /// Null means no header filter.
    /// </summary>
    public HeaderSearch? RequestHeaderSearch { get; init; }

    /// <summary>
    /// Filter by a response header name and/or value.
    /// Null means no response header filter.
    /// </summary>
    public HeaderSearch? ResponseHeaderSearch { get; init; }

    // -------------------------------------------------------------------------
    // Response body
    // -------------------------------------------------------------------------

    /// <summary>
    /// Case-insensitive substring that must appear in the response body.
    /// Null means no body filter.
    /// </summary>
    public string? ResponseBodyContains { get; init; }

    // -------------------------------------------------------------------------
    // Request-scoped
    // -------------------------------------------------------------------------

    /// <summary>
    /// When set, restricts results to history entries for this specific saved request.
    /// Used by the per-request recent history strip.
    /// </summary>
    public Guid? RequestId { get; init; }

    // -------------------------------------------------------------------------
    // Method
    // -------------------------------------------------------------------------

    /// <summary>
    /// Case-insensitive substring match against the HTTP method (or transport-specific
    /// method string, e.g. "GET", "ws:connect", "grpc:unary").
    /// Null means no method filter.
    /// </summary>
    public string? Method { get; init; }

    // -------------------------------------------------------------------------
    // Elapsed time
    // -------------------------------------------------------------------------

    /// <summary>
    /// Minimum round-trip time in milliseconds (inclusive).
    /// Null means no lower bound.
    /// </summary>
    public long? MinElapsedMs { get; init; }

    /// <summary>
    /// Maximum round-trip time in milliseconds (inclusive).
    /// Null means no upper bound.
    /// </summary>
    public long? MaxElapsedMs { get; init; }
}

/// <summary>Controls how a URL pattern is matched.</summary>
public enum UrlMatchMode
{
    /// <summary>The URL must contain the pattern string (default).</summary>
    Contains,

    /// <summary>The URL must start with the pattern string.</summary>
    StartsWith,

    /// <summary>The pattern is treated as a regular expression.</summary>
    Regex,
}

/// <summary>A name and/or value pair used to search through request or response headers.</summary>
/// <param name="Name">Header name to search for. Null means match any name.</param>
/// <param name="Value">Header value to match (case-insensitive contains). Null means match any value.</param>
public sealed record HeaderSearch(string? Name, string? Value);
