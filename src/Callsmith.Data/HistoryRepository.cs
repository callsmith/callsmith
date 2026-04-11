using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Callsmith.Data;

/// <summary>
/// SQLite-backed implementation of <see cref="IHistoryService"/>.
/// Each collection has its own isolated SQLite database; this repository switches
/// target databases when <see cref="SetCollectionAsync"/> is called.
/// </summary>
public sealed class HistoryRepository : IHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string MaskedValue = "****";
    private const string HistoryTableName = "HistoryEntries";

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    // Tracks which database files have already been schema-checked in the current process
    // lifetime. HistoryRepository is registered as a singleton; the user may switch between
    // multiple collections in one session, each backed by its own SQLite file. The set is
    // append-only (paths are never removed), so in the rare case that a database file is
    // deleted and recreated mid-session the schema check will not re-run. This is accepted
    // as an unlikely edge case; a full re-run requires restarting the application.
    private readonly ConcurrentDictionary<string, byte> _checkedDbPaths = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHistoryEncryptionService _encryption;
    private readonly ILogger<HistoryRepository> _logger;

    // Null when no collection is active; set by SetCollectionAsync.
    private string? _currentDbPath;

    public HistoryRepository(
        IHistoryEncryptionService encryption,
        ILogger<HistoryRepository> logger)
    {
        _encryption = encryption;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task SetCollectionAsync(string? collectionFolderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collectionFolderPath))
        {
            _currentDbPath = null;
        }
        else
        {
            var dbPath = CallsmithDbContext.GetDbPath(collectionFolderPath);
            var dir = Path.GetDirectoryName(dbPath)!;
            Directory.CreateDirectory(dir);
            _currentDbPath = dbPath;
        }

        return Task.CompletedTask;
    }

    // Creates a DbContext pointed at the current collection's database.
    private CallsmithDbContext CreateDbContext()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _currentDbPath,
        }.ToString();

        var options = new DbContextOptionsBuilder<CallsmithDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new CallsmithDbContext(options);
    }

    /// <inheritdoc/>
    public async Task RecordAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_currentDbPath is null) return;

        var entity = ToEntity(entry);

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);

        db.HistoryEntries.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)> QueryAsync(
        HistoryFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (_currentDbPath is null) return ([], 0);

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);

        var query = db.HistoryEntries.AsNoTracking().AsQueryable();

        query = ApplyIndexedFilters(query, filter);

        var totalCount = await query.LongCountAsync(ct);

        query = filter.NewestFirst
            ? query.OrderByDescending(e => e.SentAtUnixMs).ThenByDescending(e => e.Id)
            : query.OrderBy(e => e.SentAtUnixMs).ThenBy(e => e.Id);

        var pageSize = Math.Max(1, filter.PageSize);
        var page = Math.Max(0, filter.Page);

        query = query.Skip(page * pageSize).Take(pageSize);

        var entities = await query.ToListAsync(ct);
        var entries = entities.Select(ToDomainMasked).ToList();

        return (entries, totalCount);
    }

    /// <inheritdoc/>
    public async Task<HistoryEntry?> GetLatestForRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        if (_currentDbPath is null) return null;

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);

        var entity = await db.HistoryEntries
            .AsNoTracking()
            .Where(e => e.RequestId == requestId)
            .OrderByDescending(e => e.SentAtUnixMs)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToDomainMasked(entity);
    }

    /// <inheritdoc/>
    public async Task<HistoryEntry?> GetLatestForRequestInEnvironmentAsync(
        Guid requestId,
        Guid? environmentId,
        CancellationToken ct = default)
    {
        if (_currentDbPath is null) return null;

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);

        var query = db.HistoryEntries
            .AsNoTracking()
            .Where(e => e.RequestId == requestId);

        if (!environmentId.HasValue)
        {
            query = query.Where(e => e.EnvironmentId == null);
        }
        else
        {
            query = query.Where(e => e.EnvironmentId == environmentId.Value);
        }

        var entity = await query
            .OrderByDescending(e => e.SentAtUnixMs)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToDomainMasked(entity);
    }

    /// <inheritdoc/>
    public async Task<HistoryEntry?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        if (_currentDbPath is null) return null;

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);

        var entity = await db.HistoryEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        return entity is null ? null : ToDomainMasked(entity);
    }

    /// <inheritdoc/>
    public async Task<long> GetCountAsync(CancellationToken ct = default)
    {
        if (_currentDbPath is null) return 0;

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);
        return await db.HistoryEntries.LongCountAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetEnvironmentNamesAsync(
        Guid? requestId = null,
        CancellationToken ct = default)
    {
        var options = await GetEnvironmentOptionsAsync(requestId, ct);
        return options.Select(static o => o.Name).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HistoryEnvironmentOption>> GetEnvironmentOptionsAsync(
        Guid? requestId = null,
        CancellationToken ct = default)
    {
        if (_currentDbPath is null) return [];

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);

        var query = db.HistoryEntries
            .AsNoTracking()
            .Where(e => e.EnvironmentName != null && e.EnvironmentName != string.Empty);

        if (requestId.HasValue)
            query = query.Where(e => e.RequestId == requestId.Value);

        var rows = await query
            .OrderByDescending(e => e.SentAtUnixMs)
            .Select(e => new { e.EnvironmentName, e.EnvironmentId, e.EnvironmentColor })
            .ToListAsync(ct);

        var map = new Dictionary<string, (Guid? Id, string? Color)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var name = row.EnvironmentName;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!map.TryGetValue(name, out var existing))
            {
                map[name] = (row.EnvironmentId, string.IsNullOrWhiteSpace(row.EnvironmentColor) ? null : row.EnvironmentColor);
                continue;
            }

            var id = existing.Id ?? row.EnvironmentId;
            var color = existing.Color;
            if (string.IsNullOrWhiteSpace(color) && !string.IsNullOrWhiteSpace(row.EnvironmentColor))
                color = row.EnvironmentColor;

            map[name] = (id, color);
        }

        return map
            .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static kv => new HistoryEnvironmentOption
            {
                Name = kv.Key,
                Id = kv.Value.Id,
                Color = kv.Value.Color,
            })
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<HistoryEntry> RevealSensitiveFieldsAsync(
        HistoryEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var revealedBindings = entry.VariableBindings
            .Select(b =>
            {
                if (!b.IsSecret || b.CiphertextValue is null)
                    return b;

                try
                {
                    var plaintext = _encryption.Decrypt(b.CiphertextValue);
                    return b with { ResolvedValue = plaintext };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt secret binding for token {Token}.", b.Token);
                    return b;
                }
            })
            .ToList();

        return await Task.FromResult(entry with { VariableBindings = revealedBindings });
    }

    /// <inheritdoc/>
    public async Task DeleteByIdAsync(long id, CancellationToken ct = default)
    {
        if (_currentDbPath is null) return;

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);

        await db.HistoryEntries
            .Where(e => e.Id == id)
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc/>
    public async Task PurgeOlderThanAsync(
        DateTimeOffset cutoff,
        string? environmentName = null,
        Guid? requestId = null,
        CancellationToken ct = default)
    {
        if (_currentDbPath is null) return;

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);
        var cutoffUnixMs = cutoff.ToUnixTimeMilliseconds();
        var query = db.HistoryEntries
            .Where(e => e.SentAtUnixMs < cutoffUnixMs);

        if (!string.IsNullOrWhiteSpace(environmentName))
            query = query.Where(e => e.EnvironmentName == environmentName);

        if (requestId.HasValue)
            query = query.Where(e => e.RequestId == requestId.Value);

        await query
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc/>
    public async Task PurgeAllAsync(
        string? environmentName = null,
        Guid? requestId = null,
        CancellationToken ct = default)
    {
        if (_currentDbPath is null) return;

        await using var db = CreateDbContext();
        await EnsureHistorySchemaAsync(db, ct);
        var query = db.HistoryEntries.AsQueryable();

        if (!string.IsNullOrWhiteSpace(environmentName))
            query = query.Where(e => e.EnvironmentName == environmentName);

        if (requestId.HasValue)
            query = query.Where(e => e.RequestId == requestId.Value);

        await query.ExecuteDeleteAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private HistoryEntryEntity ToEntity(HistoryEntry entry)
    {
        var bindingDtos = entry.VariableBindings
            .Select(b => new BindingDto(
                b.Token,
                b.IsSecret ? _encryption.Encrypt(b.ResolvedValue) : b.ResolvedValue,
                b.IsSecret))
            .ToList();

        return new HistoryEntryEntity
        {
            Id = entry.Id,
            RequestId = entry.RequestId,
            SentAt = entry.SentAt,
            SentAtUnixMs = entry.SentAt.ToUnixTimeMilliseconds(),
            Method = entry.Method,
            StatusCode = entry.StatusCode,
            ResolvedUrl = entry.ResolvedUrl,
            RequestName = entry.RequestName,
            EnvironmentName = entry.EnvironmentName,
            EnvironmentId = entry.EnvironmentId,
            EnvironmentColor = entry.EnvironmentColor,
            ElapsedMs = entry.ElapsedMs,
            RequestSearchText = BuildRequestSearchText(entry),
            ResponseSearchText = BuildResponseSearchText(entry.ResponseSnapshot),
            ConfiguredSnapshotJson = JsonSerializer.Serialize(entry.ConfiguredSnapshot, JsonOpts),
            VariableBindingsJson = JsonSerializer.Serialize(bindingDtos, JsonOpts),
            ResponseSnapshotJson = entry.ResponseSnapshot is null
                ? null
                : JsonSerializer.Serialize(entry.ResponseSnapshot, JsonOpts),
        };
    }

    private HistoryEntry ToDomainMasked(HistoryEntryEntity entity)
    {
        ConfiguredRequestSnapshot? snapshot = null;
        try
        {
            snapshot = JsonSerializer.Deserialize<ConfiguredRequestSnapshot>(
                entity.ConfiguredSnapshotJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to deserialize ConfiguredSnapshot for history entry {Id}.", entity.Id);
        }

        var bindingDtos = DeserializeBindings(entity.VariableBindingsJson, entity.Id);

        var bindings = bindingDtos
            .Select(dto => dto.IsSecret
                ? new VariableBinding(dto.Token, MaskedValue, IsSecret: true, CiphertextValue: dto.ResolvedValue)
                : new VariableBinding(dto.Token, dto.ResolvedValue, IsSecret: false))
            .ToList();

        ResponseSnapshot? response = null;
        if (entity.ResponseSnapshotJson is not null)
        {
            try
            {
                response = JsonSerializer.Deserialize<ResponseSnapshot>(
                    entity.ResponseSnapshotJson, JsonOpts);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deserialize ResponseSnapshot for history entry {Id}.", entity.Id);
            }
        }

        return new HistoryEntry
        {
            Id = entity.Id,
            RequestId = entity.RequestId,
            SentAt = entity.SentAt,
            Method = entity.Method,
            StatusCode = entity.StatusCode,
            ResolvedUrl = entity.ResolvedUrl,
            RequestName = entity.RequestName,
            EnvironmentName = entity.EnvironmentName,
            EnvironmentId = entity.EnvironmentId,
            EnvironmentColor = entity.EnvironmentColor,
            ElapsedMs = entity.ElapsedMs,
            ConfiguredSnapshot = snapshot ?? new ConfiguredRequestSnapshot
            {
                Method = entity.Method,
                Url = entity.ResolvedUrl,
            },
            VariableBindings = bindings,
            ResponseSnapshot = response,
        };
    }

    private List<BindingDto> DeserializeBindings(string json, long entryId)
    {
        try
        {
            return JsonSerializer.Deserialize<List<BindingDto>>(json, JsonOpts) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to deserialize VariableBindings for history entry {Id}.", entryId);
            return [];
        }
    }

    private static IQueryable<HistoryEntryEntity> ApplyIndexedFilters(
        IQueryable<HistoryEntryEntity> query,
        HistoryFilter filter)
    {
        if (filter.SentAfter.HasValue)
            query = query.Where(e => e.SentAtUnixMs >= filter.SentAfter.Value.ToUnixTimeMilliseconds());

        if (filter.SentBefore.HasValue)
            query = query.Where(e => e.SentAtUnixMs <= filter.SentBefore.Value.ToUnixTimeMilliseconds());

        if (filter.MinStatusCode.HasValue)
            query = query.Where(e => e.StatusCode >= filter.MinStatusCode.Value);

        if (filter.MaxStatusCode.HasValue)
            query = query.Where(e => e.StatusCode <= filter.MaxStatusCode.Value);

        if (filter.RequestId.HasValue)
            query = query.Where(e => e.RequestId == filter.RequestId.Value);

        if (!string.IsNullOrWhiteSpace(filter.RequestName))
            query = query.Where(e =>
                e.RequestName != null && e.RequestName.Contains(filter.RequestName));

        if (filter.NoEnvironment)
        {
            query = query.Where(e => e.EnvironmentId == null &&
                (e.EnvironmentName == null || e.EnvironmentName == string.Empty));
        }
        else if (filter.EnvironmentId.HasValue)
        {
            query = query.Where(e => e.EnvironmentId == filter.EnvironmentId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(filter.EnvironmentName))
        {
            var env = NormalizeSearchText(filter.EnvironmentName);
            query = query.Where(e =>
                e.EnvironmentName != null &&
                EF.Functions.Like(e.EnvironmentName.ToLower(), $"%{env}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.TextSearch))
        {
            var text = NormalizeSearchText(filter.TextSearch);
            query = query.Where(e =>
                e.ResolvedUrl.Contains(text) ||
                (e.RequestName != null && e.RequestName.Contains(text)));
        }

        if (!string.IsNullOrWhiteSpace(filter.RequestContains))
        {
            var text = NormalizeSearchText(filter.RequestContains);
            query = query.Where(e => e.RequestSearchText.Contains(text));
        }

        if (!string.IsNullOrWhiteSpace(filter.ResponseContains))
        {
            var text = NormalizeSearchText(filter.ResponseContains);
            query = query.Where(e => e.ResponseSearchText.Contains(text));
        }

        if (!string.IsNullOrWhiteSpace(filter.GlobalSearch))
        {
            var text = NormalizeSearchText(filter.GlobalSearch);
            query = query.Where(e =>
                e.RequestSearchText.Contains(text) ||
                e.ResponseSearchText.Contains(text));
        }

        if (!string.IsNullOrWhiteSpace(filter.Method))
        {
            var method = NormalizeSearchText(filter.Method);
            query = query.Where(e => e.Method.ToLower().Contains(method));
        }

        if (filter.MinElapsedMs.HasValue)
            query = query.Where(e => e.ElapsedMs >= filter.MinElapsedMs.Value);

        if (filter.MaxElapsedMs.HasValue)
            query = query.Where(e => e.ElapsedMs <= filter.MaxElapsedMs.Value);

        if (!string.IsNullOrWhiteSpace(filter.UrlPattern))
        {
            query = filter.UrlMatch switch
            {
                UrlMatchMode.StartsWith => query.Where(e => e.ResolvedUrl.StartsWith(filter.UrlPattern)),
                // Regex matching requires client-side evaluation; Contains used as fallback.
                _ => query.Where(e => e.ResolvedUrl.Contains(filter.UrlPattern)),
            };
        }

        return query;
    }

    private async Task EnsureHistorySchemaAsync(CallsmithDbContext db, CancellationToken ct)
    {
        // _currentDbPath is guaranteed non-null by callers that already checked it.
        var dbPath = _currentDbPath!;

        if (_checkedDbPaths.ContainsKey(dbPath)) return;

        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_checkedDbPaths.ContainsKey(dbPath)) return;

            // Manual schema migration instead of EF Core Migrations:
            // History databases are per-collection and created on first use. EF Core Migrations
            // require a single canonical migrations table and do not compose well with
            // per-user, per-collection SQLite files distributed across arbitrary directories.
            // Instead we use EnsureCreated to create the initial schema and ALTER TABLE
            // statements to add columns introduced in later versions. This approach is
            // append-only (columns are never dropped or renamed) which makes it safe and simple.
            // If a full schema overhaul is ever needed, a versioned migration table should be
            // introduced at that point.

            await db.Database.EnsureCreatedAsync(ct);

            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(ct);

            var hasSentAtUnixMs = false;
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info('{HistoryTableName}');";
                await using var reader = await pragma.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    // PRAGMA table_info result: cid, name, type, notnull, dflt_value, pk
                    if (string.Equals(reader.GetString(1), "SentAtUnixMs", StringComparison.Ordinal))
                    {
                        hasSentAtUnixMs = true;
                        break;
                    }
                }
            }

            if (!hasSentAtUnixMs)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText =
                    $"ALTER TABLE {HistoryTableName} ADD COLUMN SentAtUnixMs INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync(ct);

                // Backfill existing rows from SentAt for stable ordering of pre-upgrade history.
                await using var backfill = connection.CreateCommand();
                backfill.CommandText =
                    $"UPDATE {HistoryTableName} SET SentAtUnixMs = CAST(strftime('%s', SentAt) AS INTEGER) * 1000 WHERE SentAtUnixMs = 0;";
                await backfill.ExecuteNonQueryAsync(ct);
            }

            await EnsureSearchTextColumnsAsync(connection, ct);
            await EnsureEnvironmentNameColumnAsync(connection, ct);
            await EnsureEnvironmentIdColumnAsync(connection, ct);
            await EnsureEnvironmentColorColumnAsync(connection, ct);

            await BackfillSearchTextAsync(db, ct);

            await using var index = connection.CreateCommand();
            index.CommandText =
                $"CREATE INDEX IF NOT EXISTS IX_{HistoryTableName}_SentAtUnixMs ON {HistoryTableName} (SentAtUnixMs);";
            await index.ExecuteNonQueryAsync(ct);

            _checkedDbPaths.TryAdd(dbPath, 0);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task EnsureSearchTextColumnsAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        var hasRequestSearchText = false;
        var hasResponseSearchText = false;

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info('{HistoryTableName}');";
            await using var reader = await pragma.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(1);
                if (string.Equals(name, nameof(HistoryEntryEntity.RequestSearchText), StringComparison.Ordinal))
                    hasRequestSearchText = true;
                else if (string.Equals(name, nameof(HistoryEntryEntity.ResponseSearchText), StringComparison.Ordinal))
                    hasResponseSearchText = true;
            }
        }

        if (!hasRequestSearchText)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE {HistoryTableName} ADD COLUMN RequestSearchText TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct);
        }

        if (!hasResponseSearchText)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE {HistoryTableName} ADD COLUMN ResponseSearchText TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureEnvironmentNameColumnAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        var hasEnvironmentName = false;

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info('{HistoryTableName}');";
        await using var reader = await pragma.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), nameof(HistoryEntryEntity.EnvironmentName), StringComparison.Ordinal))
            {
                hasEnvironmentName = true;
                break;
            }
        }

        if (!hasEnvironmentName)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE {HistoryTableName} ADD COLUMN EnvironmentName TEXT NULL;";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureEnvironmentColorColumnAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        var hasEnvironmentColor = false;

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info('{HistoryTableName}');";
        await using var reader = await pragma.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), nameof(HistoryEntryEntity.EnvironmentColor), StringComparison.Ordinal))
            {
                hasEnvironmentColor = true;
                break;
            }
        }

        if (!hasEnvironmentColor)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE {HistoryTableName} ADD COLUMN EnvironmentColor TEXT NULL;";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureEnvironmentIdColumnAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        var hasEnvironmentId = false;

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info('{HistoryTableName}');";
        await using var reader = await pragma.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), nameof(HistoryEntryEntity.EnvironmentId), StringComparison.Ordinal))
            {
                hasEnvironmentId = true;
                break;
            }
        }

        if (!hasEnvironmentId)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE {HistoryTableName} ADD COLUMN EnvironmentId TEXT NULL;";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task BackfillSearchTextAsync(CallsmithDbContext db, CancellationToken ct)
    {
        // Process in batches to avoid loading an unbounded number of rows into memory.
        // A user with months of history could have tens of thousands of rows needing backfill.
        const int batchSize = 200;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var rows = await db.HistoryEntries
                .Where(e => e.RequestSearchText == "" || e.ResponseSearchText == "")
                .Take(batchSize)
                .ToListAsync(ct);

            if (rows.Count == 0)
                return;

        foreach (var row in rows)
        {
            ConfiguredRequestSnapshot? snapshot = null;
            ResponseSnapshot? response = null;

            try
            {
                snapshot = JsonSerializer.Deserialize<ConfiguredRequestSnapshot>(row.ConfiguredSnapshotJson, JsonOpts);
            }
            catch (JsonException) { }

            if (row.ResponseSnapshotJson is not null)
            {
                try
                {
                    response = JsonSerializer.Deserialize<ResponseSnapshot>(row.ResponseSnapshotJson, JsonOpts);
                }
                catch (JsonException) { }
            }

            if (string.IsNullOrEmpty(row.RequestSearchText))
            {
                row.RequestSearchText = BuildRequestSearchText(new HistoryEntry
                {
                    Method = row.Method,
                    ResolvedUrl = row.ResolvedUrl,
                    RequestName = row.RequestName,
                    EnvironmentName = row.EnvironmentName,
                    ConfiguredSnapshot = snapshot ?? new ConfiguredRequestSnapshot
                    {
                        Method = row.Method,
                        Url = row.ResolvedUrl,
                    },
                });
            }

            if (string.IsNullOrEmpty(row.ResponseSearchText))
                row.ResponseSearchText = BuildResponseSearchText(response);
        }

        await db.SaveChangesAsync(ct);
        }
    }

    private static string BuildRequestSearchText(HistoryEntry entry)
    {
        var snapshot = entry.ConfiguredSnapshot;
        var builder = new StringBuilder();

        Append(builder, entry.RequestName);
        Append(builder, entry.EnvironmentName);
        Append(builder, entry.Method);
        Append(builder, entry.ResolvedUrl);
        Append(builder, snapshot.Url);
        AppendKvList(builder, snapshot.Headers);
        AppendKvList(builder, snapshot.AutoAppliedHeaders);
        AppendKvList(builder, snapshot.QueryParams);

        foreach (var part in snapshot.PathParams)
        {
            Append(builder, part.Key);
            Append(builder, part.Value);
        }

        Append(builder, snapshot.Body);

        foreach (var part in snapshot.FormParams)
        {
            Append(builder, part.Key);
            Append(builder, part.Value);
        }

        Append(builder, snapshot.Auth.AuthType);
        Append(builder, snapshot.Auth.Username);
        Append(builder, snapshot.Auth.ApiKeyName);
        Append(builder, snapshot.Auth.ApiKeyIn);

        return NormalizeSearchText(builder.ToString());
    }

    private static string BuildResponseSearchText(ResponseSnapshot? snapshot)
    {
        if (snapshot is null)
            return string.Empty;

        var builder = new StringBuilder();
        Append(builder, snapshot.FinalUrl);
        Append(builder, snapshot.ReasonPhrase);
        Append(builder, snapshot.Body);
        foreach (var header in snapshot.Headers)
        {
            Append(builder, header.Key);
            Append(builder, header.Value);
        }

        return NormalizeSearchText(builder.ToString());
    }

    private static void AppendKvList(StringBuilder builder, IEnumerable<RequestKv> items)
    {
        foreach (var item in items)
        {
            Append(builder, item.Key);
            Append(builder, item.Value);
        }
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append(value);
        builder.Append('\n');
    }

    private static string NormalizeSearchText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToLowerInvariant();

    // DTO used only for JSON serialization of the VariableBindings column.
    private sealed record BindingDto(string Token, string ResolvedValue, bool IsSecret);
}
