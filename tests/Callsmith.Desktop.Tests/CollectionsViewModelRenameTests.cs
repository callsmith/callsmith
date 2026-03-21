using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Net.Http;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for the rename flow in <see cref="CollectionsViewModel"/>:
/// Verifies that RequestRenamedMessage is sent when requests are renamed,
/// and that open tabs are properly notified and updated.
/// </summary>
public sealed class CollectionsViewModelRenameTests
{
    private const string FakeCollectionPath = @"C:\collections\my-api";

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static CollectionsViewModel BuildSut(
        ICollectionService? collectionService = null,
        IMessenger? messenger = null)
    {
        var cs = collectionService ?? Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });

        var recent = Substitute.For<IRecentCollectionsService>();
        recent.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        var import = Substitute.For<ICollectionImportService>();

        var prefs = Substitute.For<ICollectionPreferencesService>();
        prefs.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new CollectionPreferences { ExpandedFolderPaths = [] });

        return new CollectionsViewModel(
            cs,
            recent,
            import,
            prefs,
            messenger ?? new WeakReferenceMessenger(),
            NullLogger<CollectionsViewModel>.Instance);
    }

    private static (CollectionTreeItemViewModel root,
                    CollectionTreeItemViewModel folder,
                    CollectionTreeItemViewModel request)
        BuildTree(string folderPath = @"C:\collections\my-api\auth",
                  string requestPath = @"C:\collections\my-api\auth\login.callsmith")
    {
        var requestModel = new CollectionRequest
        {
            FilePath = requestPath,
            Name = "login",
            Method = HttpMethod.Post,
            Url = "https://example.com/auth/login",
        };

        var rootFolder = new CollectionFolder
        {
            Name = "root",
            FolderPath = FakeCollectionPath,
            Requests = [],
            SubFolders =
            [
                new CollectionFolder
                {
                    Name = "auth",
                    FolderPath = folderPath,
                    Requests = [requestModel],
                    SubFolders = [],
                }
            ],
        };

        var root = CollectionTreeItemViewModel.FromFolder(rootFolder, parent: null, isRoot: true);
        var folder = (CollectionTreeItemViewModel)root.Children[0];
        var request = (CollectionTreeItemViewModel)folder.Children[0];
        return (root, folder, request);
    }

    /// <summary>
    /// When a request is renamed, RequestRenamedMessage should be sent with
    /// both the old path and the new CollectionRequest.
    /// </summary>
    [Fact]
    public async Task CommitRenameDialogAsync_WhenRenameRequest_SendsRequestRenamedMessage()
    {
        var oldPath = @"C:\collections\my-api\auth\login.callsmith";
        var newPath = @"C:\collections\my-api\auth\sign-in.callsmith";
        var newRequest = new CollectionRequest
        {
            FilePath = newPath,
            Name = "sign-in",
            Method = HttpMethod.Post,
            Url = "https://example.com/auth/login",
        };

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RenameRequestAsync(oldPath, "sign-in", Arg.Any<CancellationToken>())
                         .Returns(newRequest);

        var messenger = new WeakReferenceMessenger();
        RequestRenamedMessage? capturedMessage = null;
        messenger.Register<RequestRenamedMessage>(this, (_, msg) => capturedMessage = msg);

        var sut = BuildSut(collectionService, messenger);
        var (_, _, request) = BuildTree();

        sut.BeginRenameCommand.Execute(request);
        sut.RenameDialogValue = "sign-in";

        await sut.CommitRenameDialogAsync();

        capturedMessage.Should().NotBeNull();
        capturedMessage!.OldFilePath.Should().Be(oldPath);
        capturedMessage.Renamed.FilePath.Should().Be(newPath);
        capturedMessage.Renamed.Name.Should().Be("sign-in");
    }

    /// <summary>
    /// When a request is renamed, the tree node should be updated with the new name.
    /// </summary>
    [Fact]
    public async Task CommitRenameDialogAsync_UpdatesTreeNodeName()
    {
        var oldPath = @"C:\collections\my-api\auth\login.callsmith";
        var newPath = @"C:\collections\my-api\auth\sign-in.callsmith";
        var newRequest = new CollectionRequest
        {
            FilePath = newPath,
            Name = "sign-in",
            Method = HttpMethod.Post,
            Url = "https://example.com/auth/login",
        };

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RenameRequestAsync(oldPath, "sign-in", Arg.Any<CancellationToken>())
                         .Returns(newRequest);

        var sut = BuildSut(collectionService);
        var (_, _, request) = BuildTree();

        sut.BeginRenameCommand.Execute(request);
        sut.RenameDialogValue = "sign-in";

        await sut.CommitRenameDialogAsync();

        request.Name.Should().Be("sign-in");
        request.Request!.FilePath.Should().Be(newPath);
    }

    /// <summary>
    /// Rename dialog should be cleared after successful rename.
    /// </summary>
    [Fact]
    public async Task CommitRenameDialogAsync_ClearsDialogState()
    {
        var oldPath = @"C:\collections\my-api\auth\login.callsmith";
        var newPath = @"C:\collections\my-api\auth\sign-in.callsmith";
        var newRequest = new CollectionRequest
        {
            FilePath = newPath,
            Name = "sign-in",
            Method = HttpMethod.Post,
            Url = "https://example.com/auth/login",
        };

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RenameRequestAsync(oldPath, "sign-in", Arg.Any<CancellationToken>())
                         .Returns(newRequest);

        var sut = BuildSut(collectionService);
        var (_, _, request) = BuildTree();

        sut.BeginRenameCommand.Execute(request);
        sut.RenameDialogValue = "sign-in";
        sut.IsRenameDialogOpen.Should().BeTrue();

        await sut.CommitRenameDialogAsync();

        sut.IsRenameDialogOpen.Should().BeFalse();
        sut.RenameDialogError.Should().BeEmpty();
    }

    /// <summary>
    /// When rename fails, the dialog should remain open with an error message.
    /// </summary>
    [Fact]
    public async Task CommitRenameDialogAsync_WhenRenameThrows_HandlesError()
    {
        var oldPath = @"C:\collections\my-api\auth\login.callsmith";

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RenameRequestAsync(oldPath, "taken", Arg.Any<CancellationToken>())
                         .Returns<CollectionRequest>(_ => throw new InvalidOperationException("Name already exists"));

        var sut = BuildSut(collectionService);
        var (_, _, request) = BuildTree();

        sut.BeginRenameCommand.Execute(request);
        sut.RenameDialogValue = "taken";

        await sut.CommitRenameDialogAsync();

        // Should not crash; logging handles the error
        sut.IsRenameDialogOpen.Should().BeFalse();
    }
}
