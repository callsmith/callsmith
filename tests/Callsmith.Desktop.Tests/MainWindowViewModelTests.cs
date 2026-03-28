using Callsmith.Core.Abstractions;
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
}
