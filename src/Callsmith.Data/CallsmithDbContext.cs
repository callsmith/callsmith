using System.Security.Cryptography;
using System.Text;
using Callsmith.Core.Helpers;
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
    }

    /// <summary>
    /// Returns the platform-appropriate path for the collection-specific SQLite database file.
    /// The database file is named after a SHA-256 hash of the normalised collection folder path,
    /// stored under <c>%APPDATA%\Callsmith\history\</c>.
    /// </summary>
    /// <param name="collectionFolderPath">Absolute path to the collection root folder.</param>
    public static string GetDbPath(string collectionFolderPath)
    {
        var fileName = HashPath(collectionFolderPath) + ".db";
        return Path.Combine(AppDataPaths.GetCallsmithAppDataDirectory(), "history", fileName);
    }

    /// <summary>
    /// Returns the platform-appropriate path for the AES encryption key file.
    /// </summary>
    internal static string GetKeyPath()
    {
        return Path.Combine(AppDataPaths.GetCallsmithAppDataDirectory(), "history.key");
    }

    private static string HashPath(string path)
    {
        var normalised = Path.GetFullPath(path)
                             .ToLowerInvariant()
                             .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
