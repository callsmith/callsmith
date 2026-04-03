using System.Security.Cryptography;
using System.Text;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Low-level filesystem utilities shared across collection services.
/// </summary>
internal static class FileSystemHelper
{
    /// <summary>
    /// Deletes <paramref name="path"/> and all of its contents, first clearing
    /// read-only and other restrictive attributes on every nested entry.
    /// <para>
    /// Plain <see cref="System.IO.Directory.Delete(string,bool)"/> throws
    /// <see cref="System.IO.IOException"/> ("Access to the path is denied") on
    /// Windows when any file or sub-directory inside has the read-only flag set —
    /// a common situation inside OneDrive-synced folders. Clearing attributes
    /// first avoids this.
    /// </para>
    /// </summary>
    internal static void DeleteDirectoryRobust(string path)
    {
        // Clear the root directory's own attributes first.
        TryClearAttributes(path);

        // Clear every nested entry's attributes.
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            TryClearAttributes(entry);

        Directory.Delete(path, recursive: true);
    }

    private static void TryClearAttributes(string entry)
    {
        try
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>
    /// Returns a stable hex string derived from the normalised absolute path of
    /// <paramref name="collectionFolderPath"/>. Two paths that resolve to the same
    /// directory (after <see cref="Path.GetFullPath"/>, lowercasing, and trailing
    /// separator trimming) always produce the same hash.
    /// </summary>
    internal static string HashCollectionPath(string collectionFolderPath)
    {
        var normalised = Path.GetFullPath(collectionFolderPath)
            .ToLowerInvariant()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(hash);
    }
}
