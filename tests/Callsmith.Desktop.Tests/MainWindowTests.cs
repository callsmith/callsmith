using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Messages;
using Callsmith.Desktop.ViewModels;
using Callsmith.Desktop.Views;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class MainWindowTests
{
    private static readonly string FakeCollectionPath = Path.Combine("collections", "my-api");

    [AvaloniaFact]
    public async Task Close_WhenSessionPersistenceIsAlreadyInProgress_DoesNotStartSecondPersist()
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
        var persistOperationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistOperationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistCallCount = 0;

        preferencesService.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionPreferences());

        preferencesService.UpdateAsync(Arg.Any<string>(), Arg.Any<Func<CollectionPreferences, CollectionPreferences>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref persistCallCount) == 1)
                {
                    persistOperationStarted.TrySetResult();
                    await persistOperationGate.Task;
                }
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

        var viewModel = new MainWindowViewModel(
            collections, requestEditor, environment, environmentEditor,
            commandPalette, historyPanel, messenger);

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        messenger.Send(new CollectionOpenedMessage(FakeCollectionPath));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        window.Close();
        await persistOperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        window.Close();
        Dispatcher.UIThread.RunJobs();

        persistCallCount.Should().Be(1);

        persistOperationGate.TrySetResult();
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }
}
