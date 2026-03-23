using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Stores, queries, and manages the permanent request history log.
/// Every HTTP request sent by the application is recorded as a <see cref="HistoryEntry"/>.
/// History is permanent by default; deletion is always user-initiated.
/// </summary>
public interface IHistoryService
{
    // -------------------------------------------------------------------------
    // Write
    // -------------------------------------------------------------------------

    /// <summary>
    /// Persists a new history entry. Called fire-and-forget after every successful
    /// (or error) HTTP exchange. Must not throw — implementations should log and swallow
    /// any internal errors so the caller's request UX is never disrupted.
    /// </summary>
    Task RecordAsync(HistoryEntry entry, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a paged, filtered list of history entries.
    /// </summary>
    /// <returns>
    /// A tuple of the matching entries for the current page and the total count of all
    /// matching entries (for pagination controls).
    /// </returns>
    Task<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)> QueryAsync(
        HistoryFilter filter,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the single most-recent history entry for the given saved request, or
    /// <see langword="null"/> if no history exists for it. Used to hydrate the response
    /// pane when opening a saved request tab.
    /// </summary>
    Task<HistoryEntry?> GetLatestForRequestAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Returns the single most-recent history entry for the given saved request within the
    /// specified environment scope, or <see langword="null"/> if no matching history exists.
    /// When <paramref name="environmentName"/> is <see langword="null"/>, this matches only
    /// entries recorded with no active environment.
    /// </summary>
    Task<HistoryEntry?> GetLatestForRequestInEnvironmentAsync(
        Guid requestId,
        string? environmentName,
        CancellationToken ct = default);

    /// <summary>Returns the history entry with the given surrogate id, or <see langword="null"/>.</summary>
    Task<HistoryEntry?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Returns the total number of stored history entries.</summary>
    Task<long> GetCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns distinct environment names seen in persisted history, optionally scoped
    /// to a single saved request. This is sourced from history storage so deleted
    /// environments remain selectable in the UI.
    /// </summary>
    Task<IReadOnlyList<string>> GetEnvironmentNamesAsync(
        Guid? requestId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns distinct environment options (name + captured color) seen in persisted history,
    /// optionally scoped to a single saved request.
    /// This is sourced from history storage so deleted environments remain selectable and can
    /// retain their historical color when available.
    /// </summary>
    Task<IReadOnlyList<HistoryEnvironmentOption>> GetEnvironmentOptionsAsync(
        Guid? requestId = null,
        CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Sensitive field reveal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a copy of <paramref name="entry"/> with the encrypted
    /// <see cref="VariableBinding.ResolvedValue"/> fields decrypted for in-memory use.
    /// The decrypted values are never written back to storage.
    /// </summary>
    Task<HistoryEntry> RevealSensitiveFieldsAsync(HistoryEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes a single history entry by surrogate id.
    /// No-op when the id does not exist.
    /// </summary>
    Task DeleteByIdAsync(long id, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Purge (user-initiated only — never called automatically)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Permanently deletes all history entries with <see cref="HistoryEntry.SentAt"/>
    /// earlier than <paramref name="cutoff"/>. Requires explicit user confirmation in the UI
    /// before this method is called.
    /// When <paramref name="environmentName"/> is provided, only entries from that
    /// environment are deleted. When <paramref name="requestId"/> is provided, only
    /// entries for that request are deleted.
    /// </summary>
    Task PurgeOlderThanAsync(
        DateTimeOffset cutoff,
        string? environmentName = null,
        Guid? requestId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes every history entry. Requires explicit user confirmation in the
    /// UI before this method is called.
    /// When <paramref name="environmentName"/> is provided, only entries from that
    /// environment are deleted. When <paramref name="requestId"/> is provided, only
    /// entries for that request are deleted.
    /// </summary>
    Task PurgeAllAsync(
        string? environmentName = null,
        Guid? requestId = null,
        CancellationToken ct = default);
}
