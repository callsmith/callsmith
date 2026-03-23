using System.Net.Http;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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

    /// <summary>
    /// When a folder is renamed, all requests under it should have RequestRenamedMessage
    /// sent for each affected request so that open tabs and environment variables are updated.
    /// </summary>
    [Fact]
    public async Task CommitRenameDialogAsync_WhenRenameFolder_SendsRequestRenamedMessageForAllAffectedRequests()
    {
        var oldFolderPath = @"C:\collections\my-api\auth";
        var newFolderPath = @"C:\collections\my-api\authentication";

        var renamedFolder = new CollectionFolder
        {
            Name = "authentication",
            FolderPath = newFolderPath,
            Requests = [],
            SubFolders = [],
        };

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RenameFolderAsync(oldFolderPath, "authentication", Arg.Any<CancellationToken>())
                         .Returns(renamedFolder);

        var messenger = new WeakReferenceMessenger();
        var capturedMessages = new List<RequestRenamedMessage>();
        messenger.Register<RequestRenamedMessage>(this, (_, msg) => capturedMessages.Add(msg));

        var sut = BuildSut(collectionService, messenger);
        var (_, folder, _) = BuildTree();

        // Manually add a second request to the folder for testing.
        // Old paths: auth/login.callsmith, auth/register.callsmith
        var registerRequest = new CollectionRequest
        {
            FilePath = @"C:\collections\my-api\auth\register.callsmith",
            Name = "register",
            Method = HttpMethod.Post,
            Url = "https://example.com/auth/register",
        };
        var registerNode = CollectionTreeItemViewModel.FromRequest(registerRequest, parent: folder);
        folder.Children.Add(registerNode);

        sut.BeginRenameCommand.Execute(folder);
        sut.RenameDialogValue = "authentication";

        await sut.CommitRenameDialogAsync();

        // Should have sent 2 RequestRenamedMessages, one for each request under the folder.
        capturedMessages.Should().HaveCount(2);

        // Verify first message (login request).
        capturedMessages[0].OldFilePath.Should().Be(@"C:\collections\my-api\auth\login.callsmith");
        capturedMessages[0].Renamed.FilePath.Should().Be(@"C:\collections\my-api\authentication\login.callsmith");
        capturedMessages[0].Renamed.Name.Should().Be("login");

        // Verify second message (register request).
        capturedMessages[1].OldFilePath.Should().Be(@"C:\collections\my-api\auth\register.callsmith");
        capturedMessages[1].Renamed.FilePath.Should().Be(@"C:\collections\my-api\authentication\register.callsmith");
        capturedMessages[1].Renamed.Name.Should().Be("register");
    }

    /// <summary>
    /// When a folder is renamed, the folder node should be updated with the new path.
    /// </summary>
    [Fact]
    public async Task CommitRenameDialogAsync_WhenRenameFolder_UpdatesFolderPath()
    {
        var oldFolderPath = @"C:\collections\my-api\auth";
        var newFolderPath = @"C:\collections\my-api\authentication";

        var renamedFolder = new CollectionFolder
        {
            Name = "authentication",
            FolderPath = newFolderPath,
            Requests = [],
            SubFolders = [],
        };

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RenameFolderAsync(oldFolderPath, "authentication", Arg.Any<CancellationToken>())
                         .Returns(renamedFolder);

        var sut = BuildSut(collectionService);
        var (_, folder, _) = BuildTree();

        sut.BeginRenameCommand.Execute(folder);
        sut.RenameDialogValue = "authentication";

        await sut.CommitRenameDialogAsync();

        folder.Name.Should().Be("authentication");
        folder.FolderPath.Should().Be(newFolderPath);
    }

    [Fact]
    public async Task CommitRenameDialogAsync_WhenRenameFolder_PreservesRequestIdsOnAffectedRequests()
    {
        var oldFolderPath = @"C:\collections\my-api\auth";
        var newFolderPath = @"C:\collections\my-api\authentication";
        var requestId = Guid.NewGuid();

        var renamedFolder = new CollectionFolder
        {
            Name = "authentication",
            FolderPath = newFolderPath,
            Requests = [],
            SubFolders = [],
        };

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RenameFolderAsync(oldFolderPath, "authentication", Arg.Any<CancellationToken>())
                         .Returns(renamedFolder);

        var sut = BuildSut(collectionService);
        var (root, folder, _) = BuildTree();
        sut.TreeRoots = [root];

        var requestNode = (CollectionTreeItemViewModel)folder.Children[0];
        requestNode.UpdateRequest(new CollectionRequest
        {
            RequestId = requestId,
            FilePath = requestNode.Request!.FilePath,
            Name = requestNode.Request.Name,
            Method = requestNode.Request.Method,
            Url = requestNode.Request.Url,
        });

        sut.BeginRenameCommand.Execute(folder);
        sut.RenameDialogValue = "authentication";

        await sut.CommitRenameDialogAsync();

        requestNode.Request!.RequestId.Should().Be(requestId);
    }

    /// <summary>
    /// When a folder is renamed and it was expanded, the persisted expanded folder paths
    /// should be updated with the new folder path (old path should no longer be in the list).
    /// </summary>
    [Fact]
    public async Task CommitRenameDialogAsync_WhenRenameFolderThatWasExpanded_PersistsNewPathToExpandedFolderPaths()
    {
        var oldFolderPath = @"C:\collections\my-api\auth";
        var newFolderPath = @"C:\collections\my-api\authentication";
        var collectionPath = FakeCollectionPath;

        var renamedFolder = new CollectionFolder
        {
            Name = "authentication",
            FolderPath = newFolderPath,
            Requests = [],
            SubFolders = [],
        };

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RenameFolderAsync(oldFolderPath, "authentication", Arg.Any<CancellationToken>())
                         .Returns(renamedFolder);

        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        preferencesService.LoadAsync(collectionPath, Arg.Any<CancellationToken>())
                          .Returns(new CollectionPreferences 
                          { 
                              ExpandedFolderPaths = ["auth"] // old path before rename
                          });
        preferencesService.UpdateAsync(Arg.Any<string>(), Arg.Any<Func<CollectionPreferences, CollectionPreferences>>(), Arg.Any<CancellationToken>())
                          .Returns(x => Task.CompletedTask);

        var sut = new CollectionsViewModel(
            collectionService,
            Substitute.For<IRecentCollectionsService>(),
            Substitute.For<ICollectionImportService>(),
            preferencesService,
            new WeakReferenceMessenger(),
            NullLogger<CollectionsViewModel>.Instance);

        // Set collection path first before building the tree
        sut.CollectionPath = collectionPath;

        var (_, folder, _) = BuildTree();
        sut.TreeRoots = [folder.Parent ?? folder];  // set to the tree

        // Expand the folder before renaming.
        folder.IsExpanded = true;

        // Act: Begin and commit rename
        sut.BeginRenameCommand.Execute(folder);
        sut.RenameDialogValue = "authentication";

        await sut.CommitRenameDialogAsync();

        // Assert: UpdateAsync should have been called with the new path in ExpandedFolderPaths
        await preferencesService.Received(1).UpdateAsync(
            Arg.Is<string>(path => path == collectionPath),
            Arg.Any<Func<CollectionPreferences, CollectionPreferences>>(),
            Arg.Any<CancellationToken>());
    }
}
