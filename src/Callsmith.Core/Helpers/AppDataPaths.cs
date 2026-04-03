namespace Callsmith.Core.Helpers;

/// <summary>
/// Resolves platform-specific application data directory paths for Callsmith.
/// </summary>
public static class AppDataPaths
{
    /// <summary>
    /// Returns the Callsmith application data directory for the current platform:
    /// <list type="bullet">
    ///   <item>Windows: <c>%APPDATA%\Callsmith</c></item>
    ///   <item>macOS: <c>~/Library/Application Support/Callsmith</c></item>
    ///   <item>Linux: <c>~/.config/Callsmith</c></item>
    /// </list>
    /// The directory is created if it does not already exist.
    /// </summary>
    public static string GetCallsmithAppDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Callsmith");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
