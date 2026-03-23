using System.ComponentModel.DataAnnotations;

namespace Callsmith.Data;

/// <summary>
/// EF Core entity representing one request history record persisted in SQLite.
/// Scalar fields that are commonly queried are stored as indexed columns.
/// Complex objects are stored as JSON blobs.
/// </summary>
internal sealed class HistoryEntryEntity
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// Stable identity of the saved request that was sent, if the request had been
    /// persisted to a collection. Null for ad-hoc sends or Bruno-loaded requests.
    /// </summary>
    public Guid? RequestId { get; set; }

    /// <summary>UTC timestamp of when the request was dispatched.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>
    /// UTC timestamp of when the request was dispatched, as Unix epoch milliseconds.
    /// SQLite can reliably sort and filter on this numeric value.
    /// </summary>
    public long SentAtUnixMs { get; set; }

    /// <summary>HTTP method string (GET, POST, …). Stored upper-case.</summary>
    [MaxLength(16)]
    public string Method { get; set; } = string.Empty;

    /// <summary>HTTP status code of the response, or null if the request failed.</summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// Fully resolved request URL (tokens substituted). Stored for full-text search.
    /// </summary>
    public string ResolvedUrl { get; set; } = string.Empty;

    /// <summary>Display name of the request, if any.</summary>
    [MaxLength(512)]
    public string? RequestName { get; set; }

    /// <summary>Display name of the collection the request belongs to, if any.</summary>
    [MaxLength(512)]
    public string? CollectionName { get; set; }

    /// <summary>Display name of the selected environment at send time, if any.</summary>
    [MaxLength(256)]
    public string? EnvironmentName { get; set; }

    /// <summary>Display color of the selected environment at send time (hex), if any.</summary>
    [MaxLength(32)]
    public string? EnvironmentColor { get; set; }

    /// <summary>
    /// Relative path of the request within the collection folder hierarchy, if any.
    /// </summary>
    public string? CollectionPath { get; set; }

    /// <summary>Total round-trip time in milliseconds.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>
    /// Lower-cased, denormalized request search text used for body/header/url searches.
    /// </summary>
    public string RequestSearchText { get; set; } = string.Empty;

    /// <summary>
    /// Lower-cased, denormalized response search text used for body/header searches.
    /// </summary>
    public string ResponseSearchText { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // JSON blob columns
    // -------------------------------------------------------------------------

    /// <summary>
    /// JSON-serialized <see cref="Callsmith.Core.Models.ConfiguredRequestSnapshot"/>.
    /// Contains the full pre-substitution request configuration.
    /// </summary>
    public string ConfiguredSnapshotJson { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialized array of variable binding objects. For bindings where
    /// <c>isSecret=true</c> the <c>resolvedValue</c> field contains AES-GCM
    /// ciphertext produced by <see cref="AesHistoryEncryptionService"/>.
    /// </summary>
    public string VariableBindingsJson { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized <see cref="Callsmith.Core.Models.ResponseSnapshot"/>, or null
    /// if the request timed out or failed before a response was received.
    /// </summary>
    public string? ResponseSnapshotJson { get; set; }
}
