using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;


namespace Callsmith.Data;

/// <summary>
/// EF Core DbContext for the Callsmith SQLite database.
/// </summary>
public sealed class CallsmithDbContext : DbContext
{
    public CallsmithDbContext(DbContextOptions<CallsmithDbContext> options) : base(options) { }

    internal DbSet<HistoryEntryEntity> HistoryEntries => Set<HistoryEntryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<HistoryEntryEntity>();

        entry.HasKey(e => e.Id);
        entry.Property(e => e.Id).ValueGeneratedOnAdd();

        // Indexes for common filter predicates.
        entry.HasIndex(e => e.SentAt);
        entry.HasIndex(e => e.SentAtUnixMs);
        entry.HasIndex(e => e.RequestId);
        entry.HasIndex(e => e.Method);
        entry.HasIndex(e => e.StatusCode);
        entry.HasIndex(e => e.CollectionName);
    }

    /// <summary>
    /// Returns the platform-appropriate path for the Callsmith SQLite database file.
    /// </summary>
    public static string GetDbPath()
    {
        var appData = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return Path.Combine(appData, "Callsmith", "data.db");
    }

    /// <summary>
    /// Returns the platform-appropriate path for the AES encryption key file.
    /// </summary>
    internal static string GetKeyPath()
    {
        var appData = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return Path.Combine(appData, "Callsmith", "history.key");
    }
}
