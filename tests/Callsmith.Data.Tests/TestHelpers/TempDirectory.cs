namespace Callsmith.Data.Tests.TestHelpers;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } =
        Directory.CreateTempSubdirectory("callsmith-data-tests-").FullName;

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
