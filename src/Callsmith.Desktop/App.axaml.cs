using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Insomnia;
using Callsmith.Core.OpenApi;
using Callsmith.Core.Postman;
using Callsmith.Core.Services;
using Callsmith.Core.Transports.Http;
using Callsmith.Data;
using Callsmith.Desktop.ViewModels;
using Callsmith.Desktop.Views;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Callsmith.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _services = ConfigureServices().BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.ShutdownRequested += (_, _) => _services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Registers all application services. Add new services here as they are implemented.
    /// </summary>
    private static ServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug));

        // Messaging (single shared instance for cross-ViewModel communication)
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        // Core -- transport
        services.AddSingleton<HttpTransport>();
        services.AddSingleton<ITransportRegistry>(sp =>
        {
            var registry = new TransportRegistry();
            registry.Register(sp.GetRequiredService<HttpTransport>());
            return registry;
        });

        // Core -- collection services (Callsmith + Bruno, routed transparently)
        services.AddSingleton<FileSystemCollectionService>();
        services.AddSingleton<BrunoCollectionService>();
        services.AddSingleton<ICollectionService, RoutingCollectionService>();

        // Core -- recent collections
        services.AddSingleton<IRecentCollectionsService, RecentCollectionsService>();

        // Core -- environment services (Callsmith + Bruno, routed transparently)
        services.AddSingleton<FileSystemEnvironmentService>();
        services.AddSingleton<IBrunoCollectionMetaService, FileSystemBrunoCollectionMetaService>();
        services.AddSingleton<BrunoEnvironmentService>();
        services.AddSingleton<IEnvironmentService, RoutingEnvironmentService>();

        // Core -- dynamic variable evaluation
        services.AddSingleton<IDynamicVariableEvaluator, DynamicVariableEvaluatorService>();

        // Core -- environment variable merge (shared algorithm used by both send and preview)
        services.AddSingleton<IEnvironmentMergeService, EnvironmentMergeService>();
        services.AddSingleton<ICollectionNamingService, CollectionNamingService>();

        // Core -- environment variable suggestions used by completion UIs
        services.AddSingleton<IEnvironmentVariableSuggestionService, EnvironmentVariableSuggestionService>();

        // Core -- request assembly (preparation of request models for sending)
        services.AddSingleton<IRequestAssemblyService, RequestAssemblyService>();

        // Core -- command palette request flattening + fuzzy filtering
        services.AddSingleton<ICommandPaletteSearchService, CommandPaletteSearchService>();

        // Core -- collection preferences
        services.AddSingleton<ICollectionPreferencesService, FileSystemCollectionPreferencesService>();

        // Core -- secret environment-variable storage (local, never checked in)
        services.AddSingleton<ISecretEncryptionService, AesSecretEncryptionService>();
        services.AddSingleton<ISecretStorageService, FileSystemSecretStorageService>();

        // Core -- import (extensible: register new importers here as formats are added)
        // NOTE: always use the plain Callsmith services here — imports must produce Callsmith-format
        // files regardless of which collection type is currently open in the routing services.
        services.AddSingleton<ICollectionImporter, PostmanCollectionImporter>();
        services.AddSingleton<ICollectionImporter, InsomniaCollectionImporter>();
        services.AddSingleton<ICollectionImporter, OpenApiCollectionImporter>();
        services.AddSingleton<ICollectionImportService>(sp => new CollectionImportService(
            sp.GetServices<ICollectionImporter>(),
            sp.GetRequiredService<FileSystemCollectionService>(),
            sp.GetRequiredService<FileSystemEnvironmentService>(),
            sp.GetRequiredService<ILogger<CollectionImportService>>()));

        // History
        services.AddSingleton<IHistoryEncryptionService, AesHistoryEncryptionService>();
        services.AddSingleton<IHistoryService, HistoryRepository>();

        // App preferences
        services.AddSingleton<IAppPreferencesService, FileSystemAppPreferencesService>();

        // Core -- JSON path evaluation (used by dynamic variables and sequence extractions)
        services.AddSingleton<IJsonPathService, JsonPathService>();

        // Core -- sequence management and execution
        services.AddSingleton<ISequenceService, FileSystemSequenceService>();
        services.AddSingleton<ISequenceRunnerService, SequenceRunnerService>();

        // ViewModels
        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton<RequestEditorViewModel>();
        services.AddSingleton<EnvironmentViewModel>();
        services.AddSingleton<EnvironmentEditorViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        services.AddSingleton<HistoryPanelViewModel>();
        services.AddSingleton<SequenceEditorViewModel>();
        services.AddSingleton<SequencesViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services;
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}