using Callsmith.Core.Abstractions;

namespace Callsmith.Core.Services;

public sealed class CollectionNamingService : ICollectionNamingService
{
    public Task<string> PickUniqueRequestNameAsync(
        string folderPath,
        string baseName,
        string requestFileExtension,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFileExtension);

        return Task.Run(() =>
        {
            var name = baseName;
            var counter = 1;

            while (File.Exists(Path.Combine(folderPath, name + requestFileExtension)))
            {
                ct.ThrowIfCancellationRequested();
                name = $"{baseName} {++counter}";
            }

            return name;
        }, ct);
    }

    public Task<string> PickUniqueFolderNameAsync(
        string parentPath,
        string baseName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

        return Task.Run(() =>
        {
            var name = baseName;
            var counter = 1;

            while (Directory.Exists(Path.Combine(parentPath, name)))
            {
                ct.ThrowIfCancellationRequested();
                name = $"{baseName} {++counter}";
            }

            return name;
        }, ct);
    }
}
