using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
    /// Returns the platform-appropriate path for the collection-specific SQLite database file.
    /// The database file is named after a SHA-256 hash of the normalised collection folder path,
    /// stored under <c>%APPDATA%\Callsmith\history\</c>.
    /// </summary>
    /// <param name="collectionFolderPath">Absolute path to the collection root folder.</param>
    public static string GetDbPath(string collectionFolderPath)
    {
        var appData = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var normalised = Path.GetFullPath(collectionFolderPath)
                             .ToLowerInvariant()
                             .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        var fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".db";

        return Path.Combine(appData, "Callsmith", "history", fileName);
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
