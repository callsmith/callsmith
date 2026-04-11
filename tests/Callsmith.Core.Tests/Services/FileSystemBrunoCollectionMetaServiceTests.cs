using Callsmith.Core.Models;
using Callsmith.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Callsmith.Core.Tests.Services;

public sealed class FileSystemBrunoCollectionMetaServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _storeDirectory;

    public FileSystemBrunoCollectionMetaServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BrunoMetaTests_" + Guid.NewGuid().ToString("N"));
        _storeDirectory = Path.Combine(_root, "meta-store");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task SaveAndLoadAsync_WhenCalledConcurrentlyForSameCollection_DoesNotLogWarningsOrCorruptMeta()
    {
        var logger = Substitute.For<ILogger<FileSystemBrunoCollectionMetaService>>();
        var sut = new FileSystemBrunoCollectionMetaService(_storeDirectory, logger);
        var collectionPath = Path.Combine(_root, "collection");
        Directory.CreateDirectory(collectionPath);

        var ids = Enumerable.Range(0, 16)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        var tasks = ids.Select((id, index) => Task.Run(async () =>
        {
            var meta = new BrunoCollectionMeta
            {
                EnvironmentIds = new Dictionary<string, Guid>
                {
                    ["Dev.bru"] = id,
                },
            };

            await sut.SaveAsync(collectionPath, meta);

            if (index % 2 == 0)
                _ = await sut.LoadAsync(collectionPath);
        }));

        await Task.WhenAll(tasks);

        var loaded = await sut.LoadAsync(collectionPath);

        loaded.EnvironmentIds.Should().ContainKey("Dev.bru");
        ids.Should().Contain(loaded.EnvironmentIds["Dev.bru"]);
        logger.ReceivedCalls().Should().BeEmpty();
    }
}