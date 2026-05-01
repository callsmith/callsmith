using System.Net.Http;
using Avalonia.Headless.XUnit;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Actions;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Verifies that <see cref="MainWindowViewModel"/> correctly exposes UndoCommand/RedoCommand
/// and dispatches undo/redo to the correct sub-ViewModel.
/// </summary>
public sealed class MainWindowViewModelUndoRedoDispatchTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (MainWindowViewModel Sut, UndoRedoService UndoService, WeakReferenceMessenger Messenger)
        BuildSut()
    {
        var messenger = new WeakReferenceMessenger();
        var undoService = new UndoRedoService();
        var collectionService = Substitute.For<ICollectionService>();
        collectionService.SaveRequestAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var recentCollectionsService = Substitute.For<IRecentCollectionsService>();
        var importService = Substitute.For<ICollectionImportService>();
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var historyService = Substitute.For<IHistoryService>();
        var transportRegistry = new TransportRegistry();
        var environmentService = Substitute.For<IEnvironmentService>();
        var dynamicEvaluator = Substitute.For<IDynamicVariableEvaluator>();
        var mergeService = new EnvironmentMergeService(dynamicEvaluator);

        var collections = new CollectionsViewModel(
            collectionService, recentCollectionsService, importService, preferencesService,
            historyService, messenger, NullLogger<CollectionsViewModel>.Instance);

        var requestEditor = new RequestEditorViewModel(
            transportRegistry, collectionService, preferencesService, dynamicEvaluator,
            mergeService, messenger, NullLogger<RequestEditorViewModel>.Instance,
            historyService: historyService, undoRedoService: undoService);

        var environment = new EnvironmentViewModel(
            environmentService, preferencesService, messenger,
            NullLogger<EnvironmentViewModel>.Instance);

        var environmentEditor = new EnvironmentEditorViewModel(
            environmentService, collectionService, dynamicEvaluator, messenger,
            NullLogger<EnvironmentEditorViewModel>.Instance,
            undoRedoService: undoService);

        var commandPalette = new CommandPaletteViewModel(collectionService, messenger);
        var historyPanel = new HistoryPanelViewModel(historyService);

        var sut = new MainWindowViewModel(
            collections, requestEditor, environment, environmentEditor,
            commandPalette, historyPanel, messenger,
            undoRedoService: undoService);

        return (sut, undoService, messenger);
    }

    private static CollectionRequest MakeRequest(string url = "https://api.example.com/") =>
        new()
        {
            FilePath = @"C:\collections\req\sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = url,
        };

    // ── CanUndo / CanRedo ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void UndoCommand_InitiallyCannotExecute()
    {
        var (sut, _, _) = BuildSut();

        sut.UndoCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact]
    public void RedoCommand_InitiallyCannotExecute()
    {
        var (sut, _, _) = BuildSut();

        sut.RedoCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact]
    public void UndoCommand_AfterPush_CanExecute()
    {
        var (sut, undo, _) = BuildSut();

        var action = Substitute.For<IUndoableAction>();
        action.ContextType.Returns("request");
        action.Description.Returns("test");
        undo.Push(action);

        sut.UndoCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact]
    public void RedoCommand_AfterUndoOnPushedAction_CanExecute()
    {
        var (sut, undo, _) = BuildSut();
        var action = Substitute.For<IUndoableAction>();
        action.ContextType.Returns("request");
        action.Description.Returns("test");
        undo.Push(action);

        undo.Undo();

        sut.RedoCommand.CanExecute(null).Should().BeTrue();
    }

    // ── Collection opened clears the stack ────────────────────────────────────

    [AvaloniaFact]
    public void CollectionOpenedMessage_ClearsUndoStack()
    {
        var (sut, undo, messenger) = BuildSut();
        var action = Substitute.For<IUndoableAction>();
        action.ContextType.Returns("request");
        action.Description.Returns("test");
        undo.Push(action);
        undo.CanUndo.Should().BeTrue();

        messenger.Send(new Messages.CollectionOpenedMessage(@"C:\new-collection"));

        undo.CanUndo.Should().BeFalse();
        sut.UndoCommand.CanExecute(null).Should().BeFalse();
    }

    // ── Request tab dispatch ──────────────────────────────────────────────────

    [AvaloniaFact]
    public void UndoCommand_WithOpenRequestTab_AppliesBeforeSnapshot()
    {
        var (sut, undo, _) = BuildSut();

        // Open a tab.
        var original = MakeRequest("https://original.example.com/");
        var tab = sut.RequestEditor.Tabs.Count == 0
            ? OpenTab(sut, original)
            : sut.RequestEditor.ActiveTab!;

        tab.Url = "https://modified.example.com/"; // pushes original→modified immediately

        // Execute undo.
        sut.UndoCommand.Execute(null);

        tab.Url.Should().Be("https://original.example.com/");
        undo.CanRedo.Should().BeTrue();
    }

    [AvaloniaFact]
    public void RedoCommand_AfterUndo_ReappliesAfterSnapshot()
    {
        var (sut, _, _) = BuildSut();

        var original = MakeRequest("https://original.example.com/");
        var tab = OpenTab(sut, original);

        tab.Url = "https://modified.example.com/"; // pushes immediately

        sut.UndoCommand.Execute(null); // restores to original

        sut.RedoCommand.Execute(null); // restores to modified

        tab.Url.Should().Be("https://modified.example.com/");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static RequestTabViewModel OpenTab(MainWindowViewModel sut, CollectionRequest request)
    {
        sut.RequestEditor.Tabs.Clear();
        sut.RequestEditor.NewTabCommand.Execute(null);
        var tab = sut.RequestEditor.ActiveTab ?? sut.RequestEditor.Tabs.LastOrDefault()!;
        tab.LoadRequest(request);
        return tab;
    }
}
