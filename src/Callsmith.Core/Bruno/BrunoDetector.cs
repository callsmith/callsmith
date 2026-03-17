namespace Callsmith.Core.Bruno;

/// <summary>
/// Lightweight helpers to detect whether a filesystem path is associated with a Bruno collection.
/// Detection is purely based on the presence of a <c>bruno.json</c> marker file.
/// </summary>
public static class BrunoDetector
{
    private const string BrunoMetaFileName = "bruno.json";

    /// <summary>
    /// Returns <c>true</c> when <paramref name="folderPath"/> is a Bruno collection root,
    /// i.e. it directly contains a <c>bruno.json</c> file.
    /// </summary>
    public static bool IsBrunoCollection(string folderPath) =>
        File.Exists(Path.Combine(folderPath, BrunoMetaFileName));

    /// <summary>Returns <c>true</c> when the path has the <c>.bru</c> extension.</summary>
    public static bool IsBrunoFile(string path) =>
        string.Equals(Path.GetExtension(path), ".bru", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> lies inside a Bruno collection —
    /// any ancestor directory contains a <c>bruno.json</c> file.
    /// </summary>
    public static bool IsUnderBrunoCollection(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, BrunoMetaFileName)))
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }
}
