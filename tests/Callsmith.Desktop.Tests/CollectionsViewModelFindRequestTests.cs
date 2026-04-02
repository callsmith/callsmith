using System.Net.Http;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for <see cref="CollectionsViewModel.FindRequestByRequestId"/>.
/// </summary>
public sealed class CollectionsViewModelFindRequestTests
{
    private const string FakeCollectionPath = @"C:\collections\my-api";

    private static CollectionsViewModel BuildSut()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });

        var recent = Substitute.For<IRecentCollectionsService>();
        recent.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        var prefs = Substitute.For<ICollectionPreferencesService>();
        prefs.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new CollectionPreferences { ExpandedFolderPaths = [] });

        return new CollectionsViewModel(
            cs,
            recent,
            Substitute.For<ICollectionImportService>(),
            prefs,
            Substitute.For<IHistoryService>(),
            new WeakReferenceMessenger(),
            NullLogger<CollectionsViewModel>.Instance);
    }

    [Fact]
    public void FindRequestByRequestId_ReturnsNull_WhenNoCollectionIsOpen()
    {
        var sut = BuildSut();
        // TreeRoots is empty by default

        var result = sut.FindRequestByRequestId(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public void FindRequestByRequestId_ReturnsRequest_WhenFoundAtRootLevel()
    {
        var requestId = Guid.NewGuid();
        var sut = BuildSut();

        var folder = new CollectionFolder
        {
            Name = "root",
            FolderPath = FakeCollectionPath,
            Requests =
            [
                new CollectionRequest
                {
                    RequestId = requestId,
                    FilePath = @"C:\collections\my-api\login.callsmith",
                    Name = "login",
                    Method = HttpMethod.Post,
                    Url = "https://example.com/login",
                },
            ],
            SubFolders = [],
        };
        sut.TreeRoots = [CollectionTreeItemViewModel.FromFolder(folder, parent: null, isRoot: true)];

        var result = sut.FindRequestByRequestId(requestId);

        result.Should().NotBeNull();
        result!.RequestId.Should().Be(requestId);
    }

    [Fact]
    public void FindRequestByRequestId_ReturnsRequest_WhenFoundInNestedSubFolder()
    {
        var requestId = Guid.NewGuid();
        var sut = BuildSut();

        var folder = new CollectionFolder
        {
            Name = "root",
            FolderPath = FakeCollectionPath,
            Requests = [],
            SubFolders =
            [
                new CollectionFolder
                {
                    Name = "auth",
                    FolderPath = @"C:\collections\my-api\auth",
                    Requests =
                    [
                        new CollectionRequest
                        {
                            RequestId = requestId,
                            FilePath = @"C:\collections\my-api\auth\login.callsmith",
                            Name = "login",
                            Method = HttpMethod.Post,
                            Url = "https://example.com/auth/login",
                        },
                    ],
                    SubFolders = [],
                },
            ],
        };
        sut.TreeRoots = [CollectionTreeItemViewModel.FromFolder(folder, parent: null, isRoot: true)];

        var result = sut.FindRequestByRequestId(requestId);

        result.Should().NotBeNull();
        result!.RequestId.Should().Be(requestId);
    }

    [Fact]
    public void FindRequestByRequestId_ReturnsNull_WhenRequestIdNotFound()
    {
        var sut = BuildSut();

        var folder = new CollectionFolder
        {
            Name = "root",
            FolderPath = FakeCollectionPath,
            Requests =
            [
                new CollectionRequest
                {
                    RequestId = Guid.NewGuid(),
                    FilePath = @"C:\collections\my-api\login.callsmith",
                    Name = "login",
                    Method = HttpMethod.Get,
                    Url = "https://example.com",
                },
            ],
            SubFolders = [],
        };
        sut.TreeRoots = [CollectionTreeItemViewModel.FromFolder(folder, parent: null, isRoot: true)];

        var result = sut.FindRequestByRequestId(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public void FindRequestByRequestId_ReturnsNull_WhenRequestHasNoRequestId()
    {
        var sut = BuildSut();

        var folder = new CollectionFolder
        {
            Name = "root",
            FolderPath = FakeCollectionPath,
            Requests =
            [
                new CollectionRequest
                {
                    RequestId = null, // no stable ID
                    FilePath = @"C:\collections\my-api\login.callsmith",
                    Name = "login",
                    Method = HttpMethod.Get,
                    Url = "https://example.com",
                },
            ],
            SubFolders = [],
        };
        sut.TreeRoots = [CollectionTreeItemViewModel.FromFolder(folder, parent: null, isRoot: true)];

        var result = sut.FindRequestByRequestId(Guid.NewGuid());

        result.Should().BeNull();
    }
}
