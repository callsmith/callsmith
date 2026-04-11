using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Data.Tests;

/// <summary>
/// Integration tests for <see cref="HistoryRepository"/>.
/// Each test uses its own isolated temp directory and SQLite database file.
/// </summary>
public sealed class HistoryRepositoryTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "callsmith-hist-repo-tests-" + Guid.NewGuid().ToString("N"));

    // Path to a fake "collection folder" used as input to SetCollectionAsync.
    private string CollectionDir => Path.Combine(_tempDir, "collection");

    public HistoryRepositoryTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(CollectionDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private HistoryRepository CreateRepository()
    {
        var keyPath = Path.Combine(_tempDir, "history.key");
        var encryption = new AesHistoryEncryptionService(keyPath);
        var logger = NullLogger<HistoryRepository>.Instance;
        return new HistoryRepository(encryption, logger);
    }

    private static HistoryEntry MakeEntry(
        Guid? requestId = null,
        string method = "GET",
        string url = "https://example.com/api",
        int? statusCode = 200,
        DateTimeOffset? sentAt = null,
        string? environmentName = null,
        Guid? environmentId = null,
        IReadOnlyList<VariableBinding>? bindings = null) =>
        new()
        {
            RequestId = requestId,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
            Method = method,
            ResolvedUrl = url,
            StatusCode = statusCode,
            ElapsedMs = 100,
            EnvironmentName = environmentName,
            EnvironmentId = environmentId,
            VariableBindings = bindings ?? [],
            ConfiguredSnapshot = new ConfiguredRequestSnapshot { Method = method, Url = url },
        };

    // ─── Collection scoping ───────────────────────────────────────────────────

    [Fact]
    public async Task SetCollectionAsync_Null_QueryReturnsEmpty()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        // Record something while active.
        await repo.RecordAsync(MakeEntry());

        // Deactivate.
        await repo.SetCollectionAsync(null);

        var (entries, count) = await repo.QueryAsync(new HistoryFilter());

        entries.Should().BeEmpty();
        count.Should().Be(0);
    }

    // ─── Record and Query ─────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_ThenQueryAsync_ReturnsEntry()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        var reqId = Guid.NewGuid();
        await repo.RecordAsync(MakeEntry(requestId: reqId, method: "POST", url: "https://example.com/items"));

        var (entries, count) = await repo.QueryAsync(new HistoryFilter());

        count.Should().Be(1);
        entries.Should().HaveCount(1);
        var entry = entries[0];
        entry.RequestId.Should().Be(reqId);
        entry.Method.Should().Be("POST");
        entry.ResolvedUrl.Should().Be("https://example.com/items");
    }

    [Fact]
    public async Task GetCountAsync_ReturnsCorrectCount()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        await repo.RecordAsync(MakeEntry());
        await repo.RecordAsync(MakeEntry());
        await repo.RecordAsync(MakeEntry());

        var count = await repo.GetCountAsync();

        count.Should().Be(3);
    }

    // ─── GetLatest ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLatestForRequestAsync_ReturnsNewestEntry()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        var reqId = Guid.NewGuid();
        var older = DateTimeOffset.UtcNow.AddMinutes(-10);
        var newer = DateTimeOffset.UtcNow;

        await repo.RecordAsync(MakeEntry(requestId: reqId, url: "https://example.com/older", sentAt: older));
        await repo.RecordAsync(MakeEntry(requestId: reqId, url: "https://example.com/newer", sentAt: newer));

        var result = await repo.GetLatestForRequestAsync(reqId);

        result.Should().NotBeNull();
        result!.ResolvedUrl.Should().Be("https://example.com/newer");
    }

    [Fact]
    public async Task GetLatestForRequestInEnvironmentAsync_ReturnsNewestForEnvironment()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        var reqId = Guid.NewGuid();
        var envId = Guid.NewGuid();
        var older = DateTimeOffset.UtcNow.AddMinutes(-5);
        var newer = DateTimeOffset.UtcNow;

        await repo.RecordAsync(MakeEntry(requestId: reqId, url: "https://example.com/old", sentAt: older, environmentId: envId));
        await repo.RecordAsync(MakeEntry(requestId: reqId, url: "https://example.com/new", sentAt: newer, environmentId: envId));

        var result = await repo.GetLatestForRequestInEnvironmentAsync(reqId, envId);

        result.Should().NotBeNull();
        result!.ResolvedUrl.Should().Be("https://example.com/new");
    }

    // ─── Secret reveal ────────────────────────────────────────────────────────

    [Fact]
    public async Task RevealSensitiveFieldsAsync_DecryptsSecretBindings()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        const string secretValue = "super-secret-token-xyz";
        var binding = new VariableBinding("{{bearerToken}}", secretValue, IsSecret: true);

        await repo.RecordAsync(MakeEntry(bindings: [binding]));

        var (entries, _) = await repo.QueryAsync(new HistoryFilter());
        var loaded = entries[0];

        // The repository should mask the secret value on load.
        loaded.VariableBindings.Should().HaveCount(1);
        var masked = loaded.VariableBindings[0];
        masked.IsSecret.Should().BeTrue();
        masked.ResolvedValue.Should().NotBe(secretValue,
            "secret values must be masked when loading from the repository");

        var revealed = await repo.RevealSensitiveFieldsAsync(loaded);

        revealed.VariableBindings[0].ResolvedValue.Should().Be(secretValue,
            "RevealSensitiveFieldsAsync should decrypt the secret back to its original value");
    }

    [Fact]
    public async Task RevealSensitiveFieldsAsync_LeavesPlaintextBindingsUnchanged()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        const string plainValue = "https://api.example.com";
        var binding = new VariableBinding("{{baseUrl}}", plainValue, IsSecret: false);

        await repo.RecordAsync(MakeEntry(bindings: [binding]));

        var (entries, _) = await repo.QueryAsync(new HistoryFilter());
        var loaded = entries[0];
        var revealed = await repo.RevealSensitiveFieldsAsync(loaded);

        revealed.VariableBindings[0].ResolvedValue.Should().Be(plainValue);
    }

    // ─── Purge ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeAllAsync_RemovesAllEntries()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        await repo.RecordAsync(MakeEntry());
        await repo.RecordAsync(MakeEntry());
        await repo.RecordAsync(MakeEntry());

        await repo.PurgeAllAsync();

        var count = await repo.GetCountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task PurgeAllAsync_WithEnvironmentFilter_OnlyRemovesMatchingEntries()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        await repo.RecordAsync(MakeEntry(environmentName: "staging"));
        await repo.RecordAsync(MakeEntry(environmentName: "staging"));
        await repo.RecordAsync(MakeEntry(environmentName: "production"));

        await repo.PurgeAllAsync(environmentName: "staging");

        var (entries, count) = await repo.QueryAsync(new HistoryFilter());

        count.Should().Be(1);
        entries[0].EnvironmentName.Should().Be("production");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_RemovesOldEntriesOnly()
    {
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        var cutoff = DateTimeOffset.UtcNow;
        await repo.RecordAsync(MakeEntry(sentAt: cutoff.AddMinutes(-5), url: "https://example.com/old1"));
        await repo.RecordAsync(MakeEntry(sentAt: cutoff.AddMinutes(-1), url: "https://example.com/old2"));
        await repo.RecordAsync(MakeEntry(sentAt: cutoff.AddMinutes(1), url: "https://example.com/new"));

        await repo.PurgeOlderThanAsync(cutoff);

        var (entries, count) = await repo.QueryAsync(new HistoryFilter());

        count.Should().Be(1);
        entries[0].ResolvedUrl.Should().Be("https://example.com/new");
    }

    // ─── Schema migration ─────────────────────────────────────────────────────

    [Fact]
    public async Task SchemaEnsure_RunsWithoutThrowingOnFreshDatabase()
    {
        // Create the repo, set the collection, and record an entry to trigger
        // EnsureHistorySchemaAsync. This covers the full migration path on a new DB.
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);

        var act = async () => await repo.RecordAsync(MakeEntry());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SchemaEnsure_RunsTwiceIdempotently()
    {
        // A second SetCollectionAsync on the same path should not throw even though
        // the schema is already set up.
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);
        await repo.RecordAsync(MakeEntry());

        // Switch away and back to force re-entry into EnsureHistorySchemaAsync logic.
        await repo.SetCollectionAsync(null);
        await repo.SetCollectionAsync(CollectionDir);

        var act = async () => await repo.RecordAsync(MakeEntry());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BackfillSearchTextAsync_PopulatesSearchTextForExistingRows()
    {
        // Create a DB directly and insert a row with empty search text columns,
        // then let the repository run its backfill.
        var dbPath = CallsmithDbContext.GetDbPath(CollectionDir);
        var dbDir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dbDir);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        var options = new DbContextOptionsBuilder<CallsmithDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using (var db = new CallsmithDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var entity = new HistoryEntryEntity
            {
                RequestId = Guid.NewGuid(),
                SentAt = DateTimeOffset.UtcNow,
                SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Method = "GET",
                ResolvedUrl = "https://example.com/backfill",
                RequestName = "BackfillTest",
                ElapsedMs = 50,
                RequestSearchText = string.Empty,
                ResponseSearchText = string.Empty,
                ConfiguredSnapshotJson = """{"method":"GET","url":"https://example.com/backfill"}""",
                VariableBindingsJson = "[]",
                ResponseSnapshotJson = null,
            };
            db.HistoryEntries.Add(entity);
            await db.SaveChangesAsync();
        }

        // Now let the repository run the full schema + backfill logic.
        var repo = CreateRepository();
        await repo.SetCollectionAsync(CollectionDir);
        await repo.RecordAsync(MakeEntry()); // triggers EnsureHistorySchemaAsync → BackfillSearchTextAsync

        // The backfilled row should now have non-empty RequestSearchText.
        await using var db2 = new CallsmithDbContext(options);
        var rows = await db2.HistoryEntries.AsNoTracking().ToListAsync();
        var backfilledRow = rows.First(r => r.RequestName == "BackfillTest");
        backfilledRow.RequestSearchText.Should().NotBeEmpty(
            "BackfillSearchTextAsync should have populated RequestSearchText for the pre-existing row");
    }
}
