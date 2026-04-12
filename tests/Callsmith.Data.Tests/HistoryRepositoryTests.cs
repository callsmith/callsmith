using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Data.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Data.Tests;

public sealed class HistoryRepositoryTests
{
    [Fact]
    public async Task QueryAsync_NoCollectionSet_ReturnsEmptyPage()
    {
        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);

        var (entries, total) = await sut.QueryAsync(new HistoryFilter());

        entries.Should().BeEmpty();
        total.Should().Be(0);
    }

    [Fact]
    public async Task GetCountAsync_NoCollectionSet_ReturnsZero()
    {
        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);

        var count = await sut.GetCountAsync();

        count.Should().Be(0);
    }

    [Fact]
    public async Task RecordAsync_NoCollectionSet_DoesNotTouchEncryption()
    {
        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);

        await sut.RecordAsync(CreateEntry());

        encryption.DidNotReceiveWithAnyArgs().Encrypt(default!);
        encryption.DidNotReceiveWithAnyArgs().Decrypt(default!);
    }

    [Fact]
    public async Task RecordThenQuery_WithActiveCollection_PersistsEntry()
    {
        using var temp = new TempDirectory();
        var collectionPath = temp.CreateSubDirectory("collection");

        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);
        await sut.SetCollectionAsync(collectionPath);

        var requestId = Guid.NewGuid();
        await sut.RecordAsync(CreateEntry(requestId: requestId, method: "POST", statusCode: 201));

        var (entries, total) = await sut.QueryAsync(new HistoryFilter { Page = 0, PageSize = 10 });

        total.Should().Be(1);
        entries.Should().ContainSingle();
        entries[0].RequestId.Should().Be(requestId);
        entries[0].Method.Should().Be("POST");
        entries[0].StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task GetLatestForRequestAsync_ReturnsNewestEntry()
    {
        using var temp = new TempDirectory();
        var collectionPath = temp.CreateSubDirectory("collection");

        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);
        await sut.SetCollectionAsync(collectionPath);

        var requestId = Guid.NewGuid();
        await sut.RecordAsync(CreateEntry(requestId: requestId, sentAt: DateTimeOffset.UtcNow.AddMinutes(-10), statusCode: 200));
        await sut.RecordAsync(CreateEntry(requestId: requestId, sentAt: DateTimeOffset.UtcNow, statusCode: 404));

        var latest = await sut.GetLatestForRequestAsync(requestId);

        latest.Should().NotBeNull();
        latest!.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetLatestForRequestInEnvironmentAsync_RespectsEnvironmentScope()
    {
        using var temp = new TempDirectory();
        var collectionPath = temp.CreateSubDirectory("collection");

        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);
        await sut.SetCollectionAsync(collectionPath);

        var requestId = Guid.NewGuid();
        var envA = Guid.NewGuid();
        var envB = Guid.NewGuid();

        await sut.RecordAsync(CreateEntry(requestId: requestId, environmentId: envA, environmentName: "Dev", statusCode: 200));
        await sut.RecordAsync(CreateEntry(requestId: requestId, environmentId: envB, environmentName: "Prod", statusCode: 500));

        var latestInDev = await sut.GetLatestForRequestInEnvironmentAsync(requestId, envA);
        var latestInProd = await sut.GetLatestForRequestInEnvironmentAsync(requestId, envB);

        latestInDev.Should().NotBeNull();
        latestInDev!.EnvironmentName.Should().Be("Dev");
        latestInDev.StatusCode.Should().Be(200);

        latestInProd.Should().NotBeNull();
        latestInProd!.EnvironmentName.Should().Be("Prod");
        latestInProd.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task PurgeOlderThanAsync_RemovesOnlyEntriesBeforeCutoff()
    {
        using var temp = new TempDirectory();
        var collectionPath = temp.CreateSubDirectory("collection");

        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);
        await sut.SetCollectionAsync(collectionPath);

        await sut.RecordAsync(CreateEntry(sentAt: DateTimeOffset.UtcNow.AddDays(-10), requestName: "old"));
        await sut.RecordAsync(CreateEntry(sentAt: DateTimeOffset.UtcNow.AddDays(-1), requestName: "new"));

        await sut.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-5));

        var (entries, total) = await sut.QueryAsync(new HistoryFilter { Page = 0, PageSize = 10 });

        total.Should().Be(1);
        entries.Should().ContainSingle();
        entries[0].RequestName.Should().Be("new");
    }

    [Fact]
    public async Task DeleteByIdAsync_RemovesTargetEntryOnly()
    {
        using var temp = new TempDirectory();
        var collectionPath = temp.CreateSubDirectory("collection");

        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);
        await sut.SetCollectionAsync(collectionPath);

        await sut.RecordAsync(CreateEntry(requestName: "first"));
        await sut.RecordAsync(CreateEntry(requestName: "second"));

        var page = await sut.QueryAsync(new HistoryFilter { Page = 0, PageSize = 10 });
        var idToDelete = page.Entries.First(e => e.RequestName == "first").Id;

        await sut.DeleteByIdAsync(idToDelete);

        var (entries, total) = await sut.QueryAsync(new HistoryFilter { Page = 0, PageSize = 10 });

        total.Should().Be(1);
        entries.Select(e => e.RequestName).Should().ContainSingle().Which.Should().Be("second");
    }

    [Fact]
    public async Task SetCollectionAsync_SwitchesActiveDatabaseScope()
    {
        using var temp = new TempDirectory();
        var collectionA = temp.CreateSubDirectory("collection-a");
        var collectionB = temp.CreateSubDirectory("collection-b");

        var encryption = Substitute.For<IHistoryEncryptionService>();
        var sut = CreateSut(encryption);

        await sut.SetCollectionAsync(collectionA);
        await sut.RecordAsync(CreateEntry(requestName: "A"));

        await sut.SetCollectionAsync(collectionB);
        await sut.RecordAsync(CreateEntry(requestName: "B"));

        var inB = await sut.QueryAsync(new HistoryFilter { Page = 0, PageSize = 10 });
        inB.TotalCount.Should().Be(1);
        inB.Entries[0].RequestName.Should().Be("B");

        await sut.SetCollectionAsync(collectionA);
        var inA = await sut.QueryAsync(new HistoryFilter { Page = 0, PageSize = 10 });
        inA.TotalCount.Should().Be(1);
        inA.Entries[0].RequestName.Should().Be("A");
    }

    private static HistoryRepository CreateSut(IHistoryEncryptionService encryption)
    {
        return new HistoryRepository(encryption, NullLogger<HistoryRepository>.Instance);
    }

    private static HistoryEntry CreateEntry(
        Guid? requestId = null,
        string method = "GET",
        int? statusCode = 200,
        DateTimeOffset? sentAt = null,
        Guid? environmentId = null,
        string? environmentName = null,
        string? requestName = null)
    {
        return new HistoryEntry
        {
            RequestId = requestId,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
            StatusCode = statusCode,
            Method = method,
            ResolvedUrl = "https://example.test/v1/orders",
            RequestName = requestName ?? "Get Orders",
            CollectionName = "Sample",
            EnvironmentId = environmentId,
            EnvironmentName = environmentName,
            CollectionPath = "C:/tmp/sample",
            ElapsedMs = 25,
            ConfiguredSnapshot = new ConfiguredRequestSnapshot
            {
                Method = method,
                Url = "https://example.test/v1/orders",
            },
            VariableBindings = [],
            ResponseSnapshot = new ResponseSnapshot
            {
                StatusCode = statusCode ?? 0,
                ReasonPhrase = "OK",
                Body = "{}",
                FinalUrl = "https://example.test/v1/orders",
                BodySizeBytes = 2,
                ElapsedMs = 25,
            },
        };
    }
}
