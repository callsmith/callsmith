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
            Substitute.For<IHistoryService>(),
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

        var (root, _, request) = BuildTree();

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
        sut.HasCollection.Should().BeTrue();
    }

      [Fact]
      public async Task MoveRequestToFolderAsync_WhenInsertAtIndexZero_WritesOrderFileWithRequestBeforeExistingItems()
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
          cs.MoveRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionRequest { FilePath = Path.Combine(FakeCollectionPath, "login.callsmith"), Name = "login", Method = System.Net.Http.HttpMethod.Post, Url = "" });

          var sut = BuildSut(cs);
          sut.CollectionPath = FakeCollectionPath;

          var (root, _, request) = BuildTree();

          // Drop the request at index 0 in root — which currently has only the "auth" sub-folder.
          // Expected order after insert: ["login.callsmith", "auth"]
          await sut.MoveRequestToFolderAsync(request, root, insertAtIndex: 0);

          await cs.Received(1).SaveFolderOrderAsync(
              root.FolderPath!,
              Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "login.callsmith", "auth" })),
              Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task MoveRequestToFolderAsync_WhenInsertAtIndexNegative_AppendsRequestAfterExistingItems()
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
          cs.MoveRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionRequest { FilePath = Path.Combine(FakeCollectionPath, "login.callsmith"), Name = "login", Method = System.Net.Http.HttpMethod.Post, Url = "" });

          var sut = BuildSut(cs);
          sut.CollectionPath = FakeCollectionPath;

          var (root, _, request) = BuildTree();

          // -1 = append at end; root currently has only "auth" sub-folder.
          // Expected order after append: ["auth", "login.callsmith"]
          await sut.MoveRequestToFolderAsync(request, root, insertAtIndex: -1);

          await cs.Received(1).SaveFolderOrderAsync(
              root.FolderPath!,
              Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "auth", "login.callsmith" })),
              Arg.Any<CancellationToken>());
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

    // ─── MoveFolderToFolderAsync ─────────────────────────────────────────────

    [Fact]
    public async Task MoveFolderToFolderAsync_CallsCollectionServiceAndReloadsTree()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });

        var sourceFolderPath = @"C:\collections\my-api\auth";
        var destFolderPath = @"C:\collections\my-api\api";
        var newFolderPath = @"C:\collections\my-api\api\auth";

        cs.MoveFolderAsync(sourceFolderPath, destFolderPath, Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { FolderPath = newFolderPath, Name = "auth", Requests = [], SubFolders = [] });

        var sut = BuildSut(cs);
        sut.CollectionPath = FakeCollectionPath;

        var (root, folder, _) = BuildTree();

        // Build a second top-level folder that is the drop target.
        var destModel = new CollectionFolder
        {
            Name = "api",
            FolderPath = destFolderPath,
            Requests = [],
            SubFolders = [],
        };
        var destNode = CollectionTreeItemViewModel.FromFolder(destModel, parent: root);

        await sut.MoveFolderToFolderAsync(folder, destNode);

        await cs.Received(1).MoveFolderAsync(sourceFolderPath, destFolderPath, Arg.Any<CancellationToken>());
        sut.HasCollection.Should().BeTrue();
    }

    [Fact]
    public async Task MoveFolderToFolderAsync_SendsRequestRenamedMessageForAllRequestsUnderFolder()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });

        var sourceFolderPath = @"C:\collections\my-api\auth";
        var destFolderPath = @"C:\collections\my-api\api";
        var newFolderPath = @"C:\collections\my-api\api\auth";

        cs.MoveFolderAsync(sourceFolderPath, destFolderPath, Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { FolderPath = newFolderPath, Name = "auth", Requests = [], SubFolders = [] });

        var messenger = new WeakReferenceMessenger();
        var capturedMessages = new List<RequestRenamedMessage>();
        messenger.Register<RequestRenamedMessage>(new object(), (_, m) => capturedMessages.Add(m));

        var sut = BuildSut(cs, messenger);
        sut.CollectionPath = FakeCollectionPath;

        var (root, folder, _) = BuildTree(folderPath: sourceFolderPath,
                                           requestPath: @"C:\collections\my-api\auth\login.callsmith");

        var destModel = new CollectionFolder
        {
            Name = "api",
            FolderPath = destFolderPath,
            Requests = [],
            SubFolders = [],
        };
        var destNode = CollectionTreeItemViewModel.FromFolder(destModel, parent: root);

        await sut.MoveFolderToFolderAsync(folder, destNode);

        capturedMessages.Should().HaveCount(1);
        capturedMessages[0].OldFilePath.Should().Be(@"C:\collections\my-api\auth\login.callsmith");
        capturedMessages[0].Renamed.FilePath.Should().Be(@"C:\collections\my-api\api\auth\login.callsmith");
    }

    [Fact]
    public async Task MoveFolderToFolderAsync_WhenInsertAtIndexZero_WritesOrderFileWithFolderBeforeExistingItems()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });

        var sourceFolderPath = @"C:\collections\my-api\auth";
        var destFolderPath = @"C:\collections\my-api\api";
        var newFolderPath = @"C:\collections\my-api\api\auth";

        cs.MoveFolderAsync(sourceFolderPath, destFolderPath, Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder { FolderPath = newFolderPath, Name = "auth", Requests = [], SubFolders = [] });

        var sut = BuildSut(cs);
        sut.CollectionPath = FakeCollectionPath;

        var (root, folder, _) = BuildTree(folderPath: sourceFolderPath);

        // Destination has one existing child folder named "users".
        var destModel = new CollectionFolder
        {
            Name = "api",
            FolderPath = destFolderPath,
            Requests = [],
            SubFolders =
            [
                new CollectionFolder { Name = "users", FolderPath = Path.Combine(destFolderPath, "users"), Requests = [], SubFolders = [] },
            ],
        };
        var destNode = CollectionTreeItemViewModel.FromFolder(destModel, parent: root);

        await sut.MoveFolderToFolderAsync(folder, destNode, insertAtIndex: 0);

        await cs.Received(1).SaveFolderOrderAsync(
            destFolderPath,
            Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "auth", "users" })),
            Arg.Any<CancellationToken>());
    }
}
