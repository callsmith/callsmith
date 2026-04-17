using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class CollectionsViewModelExpansionTests
{
    private const string CollectionPath = @"C:\collections\my-api";

    [Fact]
    public async Task CollapseAllFoldersAsync_CollapsesAllFolders_AndPersistsEmptyExpandedList()
    {
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        Func<CollectionPreferences, CollectionPreferences>? capturedUpdate = null;
        preferencesService.UpdateAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CollectionPreferences, CollectionPreferences>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedUpdate = callInfo.ArgAt<Func<CollectionPreferences, CollectionPreferences>>(1);
                return Task.CompletedTask;
            });

        var sut = BuildSut(preferencesService);
        var root = BuildTree();
        var nestedFolder = root.Children.OfType<CollectionTreeItemViewModel>().Single();
        root.IsExpanded = true;
        nestedFolder.IsExpanded = true;

        sut.CollectionPath = CollectionPath;
        sut.TreeRoots = [root];

        await sut.CollapseAllFoldersAsync();

        root.IsExpanded.Should().BeFalse();
        nestedFolder.IsExpanded.Should().BeFalse();
        await preferencesService.Received(1).UpdateAsync(
            CollectionPath,
            Arg.Any<Func<CollectionPreferences, CollectionPreferences>>(),
            Arg.Any<CancellationToken>());
        capturedUpdate.Should().NotBeNull();
        capturedUpdate!(new CollectionPreferences()).ExpandedFolderPaths.Should().BeEmpty();
    }

    private static CollectionsViewModel BuildSut(ICollectionPreferencesService preferencesService)
    {
        var collectionService = Substitute.For<ICollectionService>();
        var recent = Substitute.For<IRecentCollectionsService>();
        recent.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        return new CollectionsViewModel(
            collectionService,
            recent,
            Substitute.For<ICollectionImportService>(),
            preferencesService,
            Substitute.For<IHistoryService>(),
            new WeakReferenceMessenger(),
            NullLogger<CollectionsViewModel>.Instance);
    }

    private static CollectionTreeItemViewModel BuildTree()
    {
        var rootFolder = new CollectionFolder
        {
            Name = "root",
            FolderPath = CollectionPath,
            Requests = [],
            SubFolders =
            [
                new CollectionFolder
                {
                    Name = "auth",
                    FolderPath = Path.Combine(CollectionPath, "auth"),
                    Requests = [],
                    SubFolders = [],
                },
            ],
        };

        return CollectionTreeItemViewModel.FromFolder(rootFolder, parent: null, isRoot: true);
    }
}
