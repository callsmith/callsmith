using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for <see cref="MainWindowViewModel"/> keyboard-shortcut guards.
/// </summary>
public sealed class MainWindowViewModelTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private const string FakeCollectionPath = @"C:\collections\my-api";

    private static MainWindowViewModel BuildSut()
    {
        var messenger = new WeakReferenceMessenger();
        var collectionService = Substitute.For<ICollectionService>();
        var recentCollectionsService = Substitute.For<IRecentCollectionsService>();
        var importService = Substitute.For<ICollectionImportService>();
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var historyService = Substitute.For<IHistoryService>();
        var transportRegistry = Substitute.For<ITransportRegistry>();
        var environmentService = Substitute.For<IEnvironmentService>();
        var dynamicEvaluator = Substitute.For<IDynamicVariableEvaluator>();

        var collections = new CollectionsViewModel(
            collectionService, recentCollectionsService, importService, preferencesService,
            historyService, messenger, NullLogger<CollectionsViewModel>.Instance);

        var requestEditor = new RequestEditorViewModel(
            transportRegistry, collectionService, preferencesService, dynamicEvaluator,
            new EnvironmentMergeService(dynamicEvaluator),
            messenger, NullLogger<RequestEditorViewModel>.Instance,
            historyService: historyService);

        var environment = new EnvironmentViewModel(
            environmentService, preferencesService, messenger,
            NullLogger<EnvironmentViewModel>.Instance);

        var environmentEditor = new EnvironmentEditorViewModel(
            environmentService, collectionService, dynamicEvaluator, messenger,
            NullLogger<EnvironmentEditorViewModel>.Instance);

        var commandPalette = new CommandPaletteViewModel(collectionService, messenger);
        var historyPanel = new HistoryPanelViewModel(historyService);

        return new MainWindowViewModel(
            collections, requestEditor, environment, environmentEditor,
            commandPalette, historyPanel, messenger);
    }

    [Fact]
    public async Task Constructor_WhenRecentCollectionExists_TriggersStartupLoadAfterEnvironmentRecipientExists()
    {
        var messenger = new WeakReferenceMessenger();
        var collectionService = Substitute.For<ICollectionService>();
        var recentCollectionsService = Substitute.For<IRecentCollectionsService>();
        var importService = Substitute.For<ICollectionImportService>();
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var historyService = Substitute.For<IHistoryService>();
        var transportRegistry = Substitute.For<ITransportRegistry>();
        var environmentService = Substitute.For<IEnvironmentService>();
        var dynamicEvaluator = Substitute.For<IDynamicVariableEvaluator>();
        var environmentsLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        collectionService.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionFolder { Name = "root", FolderPath = FakeCollectionPath, Requests = [], SubFolders = [] });

        recentCollectionsService.LoadAsync(Arg.Any<CancellationToken>())
            .Returns([FakeCollectionPath]);

        preferencesService.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionPreferences { ExpandedFolderPaths = [] });

        historyService.SetCollectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        environmentService.ListEnvironmentsAsync(FakeCollectionPath, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                environmentsLoaded.TrySetResult();
                return Task.FromResult<IReadOnlyList<Callsmith.Core.Models.EnvironmentModel>>([]);
            });

        var collections = new CollectionsViewModel(
            collectionService, recentCollectionsService, importService, preferencesService,
            historyService, messenger, NullLogger<CollectionsViewModel>.Instance);

        var requestEditor = new RequestEditorViewModel(
            transportRegistry, collectionService, preferencesService, dynamicEvaluator,
            new EnvironmentMergeService(dynamicEvaluator),
            messenger, NullLogger<RequestEditorViewModel>.Instance,
            historyService: historyService);

        var environment = new EnvironmentViewModel(
            environmentService, preferencesService, messenger,
            NullLogger<EnvironmentViewModel>.Instance);

        var environmentEditor = new EnvironmentEditorViewModel(
            environmentService, collectionService, dynamicEvaluator, messenger,
            NullLogger<EnvironmentEditorViewModel>.Instance);

        var commandPalette = new CommandPaletteViewModel(collectionService, messenger);
        var historyPanel = new HistoryPanelViewModel(historyService);

        _ = new MainWindowViewModel(
            collections, requestEditor, environment, environmentEditor,
            commandPalette, historyPanel, messenger);

        await environmentsLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await environmentService.Received()
            .ListEnvironmentsAsync(FakeCollectionPath, Arg.Any<CancellationToken>());
    }

    // ─── OpenCommandPalette guard tests ──────────────────────────────────────

    [Fact]
    public void OpenCommandPalette_WhenHistoryPanelIsOpen_DoesNotOpenPalette()
    {
        var sut = BuildSut();
        sut.HistoryPanel.IsOpen = true;

        sut.OpenCommandPaletteCommand.Execute(null);

        sut.CommandPalette.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void OpenCommandPalette_WhenEnvironmentEditorIsOpen_DoesNotOpenPalette()
    {
        var sut = BuildSut();
        sut.Environment.IsEditorOpen = true;

        sut.OpenCommandPaletteCommand.Execute(null);

        sut.CommandPalette.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void OpenCommandPalette_WhenNeitherEditorNorHistoryIsOpen_OpensPalette()
    {
        var sut = BuildSut();

        sut.OpenCommandPaletteCommand.Execute(null);

        sut.CommandPalette.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void OpenEnvironmentConfiguration_WhenCollectionIsOpenAndHistoryClosed_OpensEditor()
    {
        var sut = BuildSut();
        sut.Collections.HasCollection = true;

        sut.OpenEnvironmentConfigurationCommand.Execute(null);

        sut.Environment.IsEditorOpen.Should().BeTrue();
    }

    [Fact]
    public void OpenEnvironmentConfiguration_WhenHistoryPanelIsOpen_DoesNotOpenEditor()
    {
        var sut = BuildSut();
        sut.Collections.HasCollection = true;
        sut.HistoryPanel.IsOpen = true;

        sut.OpenEnvironmentConfigurationCommand.Execute(null);

        sut.Environment.IsEditorOpen.Should().BeFalse();
    }

    [Fact]
    public void CloseCurrentTab_WhenMainEditorIsActive_StartsCloseFlowForActiveTab()
    {
        var sut = BuildSut();
        sut.Collections.HasCollection = true;
        sut.RequestEditor.NewTab();

        sut.RequestEditor.Tabs.Count.Should().Be(1);

        sut.CloseCurrentTabCommand.Execute(null);

        sut.RequestEditor.ShowCloseWithoutSavingDialog.Should().BeTrue();
        sut.RequestEditor.Tabs.Count.Should().Be(1);
        sut.RequestEditor.ActiveTab.Should().NotBeNull();
    }

    [Fact]
    public void CloseCurrentTab_WhenHistoryPanelIsOpen_DoesNotCloseActiveTab()
    {
        var sut = BuildSut();
        sut.Collections.HasCollection = true;
        sut.RequestEditor.NewTab();
        sut.HistoryPanel.IsOpen = true;

        sut.CloseCurrentTabCommand.Execute(null);

        sut.RequestEditor.ShowCloseWithoutSavingDialog.Should().BeFalse();
        sut.RequestEditor.Tabs.Count.Should().Be(1);
        sut.RequestEditor.ActiveTab.Should().NotBeNull();
    }

    [Fact]
    public void CollapseAllFolders_WhenMainRequestScreenIsActive_CollapsesTree()
    {
        var sut = BuildSut();
        var root = BuildExpandedTree();
        var nestedFolder = root.Children.OfType<CollectionTreeItemViewModel>().Single();
        sut.Collections.HasCollection = true;
        sut.Collections.CollectionPath = FakeCollectionPath;
        sut.Collections.TreeRoots = [root];

        sut.CollapseAllFoldersCommand.Execute(null);

        root.IsExpanded.Should().BeFalse();
        nestedFolder.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void CollapseAllFolders_WhenHistoryPanelIsOpen_DoesNotCollapseTree()
    {
        var sut = BuildSut();
        var root = BuildExpandedTree();
        var nestedFolder = root.Children.OfType<CollectionTreeItemViewModel>().Single();
        sut.Collections.HasCollection = true;
        sut.Collections.CollectionPath = FakeCollectionPath;
        sut.Collections.TreeRoots = [root];
        sut.HistoryPanel.IsOpen = true;

        sut.CollapseAllFoldersCommand.Execute(null);

        root.IsExpanded.Should().BeTrue();
        nestedFolder.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void CollapseAllFolders_WhenEnvironmentEditorIsOpen_DoesNotCollapseTree()
    {
        var sut = BuildSut();
        var root = BuildExpandedTree();
        var nestedFolder = root.Children.OfType<CollectionTreeItemViewModel>().Single();
        sut.Collections.HasCollection = true;
        sut.Collections.CollectionPath = FakeCollectionPath;
        sut.Collections.TreeRoots = [root];
        sut.Environment.IsEditorOpen = true;

        sut.CollapseAllFoldersCommand.Execute(null);

        root.IsExpanded.Should().BeTrue();
        nestedFolder.IsExpanded.Should().BeTrue();
    }

    private static CollectionTreeItemViewModel BuildExpandedTree()
    {
        var root = CollectionTreeItemViewModel.FromFolder(
            new CollectionFolder
            {
                Name = "root",
                FolderPath = FakeCollectionPath,
                Requests = [],
                SubFolders =
                [
                    new CollectionFolder
                    {
                        Name = "auth",
                        FolderPath = Path.Combine(FakeCollectionPath, "auth"),
                        Requests = [],
                        SubFolders = [],
                    },
                ],
            },
            parent: null,
            isRoot: true);

        root.IsExpanded = true;
        root.Children.OfType<CollectionTreeItemViewModel>().Single().IsExpanded = true;
        return root;
    }
}
