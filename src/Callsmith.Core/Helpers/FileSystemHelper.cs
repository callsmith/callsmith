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
}
