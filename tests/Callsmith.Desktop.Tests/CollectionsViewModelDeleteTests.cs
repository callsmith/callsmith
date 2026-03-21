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
/// Unit tests for the delete flow in <see cref="CollectionsViewModel"/>:
/// <see cref="CollectionsViewModel.DeleteNodeCommand"/>,
/// <see cref="CollectionsViewModel.ConfirmDeleteCommand"/>, and
/// <see cref="CollectionsViewModel.CancelDeleteCommand"/>.
/// </summary>
public sealed class CollectionsViewModelDeleteTests
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

    /// <summary>
    /// Builds a simple two-level tree:  root → [folderNode → [requestNode]].
    /// Returns the root node and both children.
    /// </summary>
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

    // ─── DeleteNode ───────────────────────────────────────────────────────────

    [Fact]
    public void DeleteNode_NonRootNode_SetsPendingDeleteNode()
    {
        var sut = BuildSut();
        var (_, folder, _) = BuildTree();

        sut.DeleteNodeCommand.Execute(folder);

        sut.PendingDeleteNode.Should().Be(folder);
    }

    [Fact]
    public void DeleteNode_RootNode_DoesNotSetPendingDeleteNode()
    {
        var sut = BuildSut();
        var (root, _, _) = BuildTree();

        sut.DeleteNodeCommand.Execute(root);

        sut.PendingDeleteNode.Should().BeNull();
    }

    [Fact]
    public void DeleteNode_RequestNode_SetsPendingDeleteNode()
    {
        var sut = BuildSut();
        var (_, _, request) = BuildTree();

        sut.DeleteNodeCommand.Execute(request);

        sut.PendingDeleteNode.Should().Be(request);
    }

    // ─── CancelDelete ────────────────────────────────────────────────────────

    [Fact]
    public void CancelDelete_ClearsPendingDeleteNode()
    {
        var sut = BuildSut();
        var (_, folder, _) = BuildTree();
        sut.DeleteNodeCommand.Execute(folder);

        sut.CancelDeleteCommand.Execute(null);

        sut.PendingDeleteNode.Should().BeNull();
    }

    // ─── ConfirmDelete — request node ────────────────────────────────────────

    [Fact]
    public async Task ConfirmDelete_RequestNode_CallsDeleteRequestAsync()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });
        cs.DeleteRequestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var sut = BuildSut(cs);
        var (_, _, request) = BuildTree();
        sut.DeleteNodeCommand.Execute(request);

        await sut.ConfirmDeleteCommand.ExecuteAsync(null);

        await cs.Received(1).DeleteRequestAsync(
            request.Request!.FilePath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmDelete_RequestNode_RemovesNodeFromParent()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });
        cs.DeleteRequestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var sut = BuildSut(cs);
        var (_, folder, request) = BuildTree();
        var parentBefore = folder.Children.Count;
        sut.DeleteNodeCommand.Execute(request);

        await sut.ConfirmDeleteCommand.ExecuteAsync(null);

        folder.Children.Should().NotContain(request);
        folder.Children.Count.Should().Be(parentBefore - 1);
    }

    [Fact]
    public async Task ConfirmDelete_RequestNode_SendsCollectionItemDeletedMessage()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });
        cs.DeleteRequestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var messenger = new WeakReferenceMessenger();
        CollectionItemDeletedMessage? received = null;
        messenger.Register<CollectionItemDeletedMessage>(new object(), (_, m) => received = m);

        var sut = BuildSut(cs, messenger);
        var (_, _, request) = BuildTree();
        var expectedPath = request.Request!.FilePath;
        sut.DeleteNodeCommand.Execute(request);

        await sut.ConfirmDeleteCommand.ExecuteAsync(null);

        received.Should().NotBeNull();
        received!.Value.Should().Be(expectedPath);
    }

    // ─── ConfirmDelete — folder node ─────────────────────────────────────────

    [Fact]
    public async Task ConfirmDelete_FolderNode_CallsDeleteFolderAsync()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });
        cs.DeleteFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var sut = BuildSut(cs);
        var (_, folder, _) = BuildTree();
        sut.DeleteNodeCommand.Execute(folder);

        await sut.ConfirmDeleteCommand.ExecuteAsync(null);

        await cs.Received(1).DeleteFolderAsync(
            folder.FolderPath!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmDelete_FolderNode_RemovesNodeFromParent()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });
        cs.DeleteFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var sut = BuildSut(cs);
        var (root, folder, _) = BuildTree();
        sut.DeleteNodeCommand.Execute(folder);

        await sut.ConfirmDeleteCommand.ExecuteAsync(null);

        root.Children.Should().NotContain(folder);
    }
    [Fact]
    public async Task MoveRequestToFolderAsync_CallsCollectionServiceAndReloadsTree()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder
          {
              Name = "root",
              FolderPath = FakeCollectionPath,
              Requests = [],
              SubFolders = [],
          });

        var sut = BuildSut(cs);
        sut.CollectionPath = FakeCollectionPath;

        var (root, folder, request) = BuildTree();

        cs.MoveRequestAsync(request.Request!.FilePath, root.FolderPath!, Arg.Any<CancellationToken>())
          .Returns(new CollectionRequest
          {
              FilePath = Path.Combine(root.FolderPath!, "login.callsmith"),
              Name = "login",
              Method = System.Net.Http.HttpMethod.Post,
              Url = "https://example.com/auth/login",
          });

        await sut.MoveRequestToFolderAsync(request, root);

        await cs.Received(1).MoveRequestAsync(request.Request.FilePath, root.FolderPath!, Arg.Any<CancellationToken>());
        sut.TreeRoots.Should().NotBeEmpty();
    }
    [Fact]
    public async Task ConfirmDelete_FolderNode_SendsMessageWithTrailingSeparator()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });
        cs.DeleteFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var messenger = new WeakReferenceMessenger();
        CollectionItemDeletedMessage? received = null;
        messenger.Register<CollectionItemDeletedMessage>(new object(), (_, m) => received = m);

        var sut = BuildSut(cs, messenger);
        var (_, folder, _) = BuildTree();
        sut.DeleteNodeCommand.Execute(folder);

        await sut.ConfirmDeleteCommand.ExecuteAsync(null);

        received.Should().NotBeNull();
        received!.Value.Should().EndWith(Path.DirectorySeparatorChar.ToString());
        received.Value.Should().StartWith(folder.FolderPath!);
    }

    // ─── ConfirmDelete — state cleanup ───────────────────────────────────────

    [Fact]
    public async Task ConfirmDelete_ClearsPendingDeleteNodeBeforeServiceCall()
    {
        var callOrder = new List<string>();

        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });
        cs.DeleteFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var sut = BuildSut(cs);
        var (_, folder, _) = BuildTree();
        sut.DeleteNodeCommand.Execute(folder);

        await sut.ConfirmDeleteCommand.ExecuteAsync(null);

        sut.PendingDeleteNode.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmDelete_WhenNoPendingNode_DoesNotCallService()
    {
        var cs = Substitute.For<ICollectionService>();
        var sut = BuildSut(cs);

        // No DeleteNodeCommand call — PendingDeleteNode is null
        await sut.ConfirmDeleteCommand.ExecuteAsync(null);

        await cs.DidNotReceive().DeleteFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cs.DidNotReceive().DeleteRequestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
