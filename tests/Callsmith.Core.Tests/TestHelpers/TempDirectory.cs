namespace Callsmith.Core.Tests.TestHelpers;

/// <summary>
/// Creates a unique temporary directory for a test and deletes it on disposal.
/// Keeps test isolation without needing a mocking framework for the filesystem.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } =
        Directory.CreateTempSubdirectory("callsmith-tests-").FullName;

    public string CreateSubDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
