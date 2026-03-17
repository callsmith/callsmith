using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Services;
using Callsmith.Core.Transports.Http;
using Callsmith.Core;
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
        services.AddSingleton<BrunoEnvironmentService>();
        services.AddSingleton<IEnvironmentService, RoutingEnvironmentService>();

        // Core -- collection preferences
        services.AddSingleton<ICollectionPreferencesService, FileSystemCollectionPreferencesService>();

        // ViewModels
        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton<RequestEditorViewModel>();
        services.AddSingleton<EnvironmentViewModel>();
        services.AddSingleton<EnvironmentEditorViewModel>();
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