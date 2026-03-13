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
        services.AddSingleton<TransportRegistry>(sp =>
        {
            var registry = new TransportRegistry();
            registry.Register(sp.GetRequiredService<HttpTransport>());
            return registry;
        });

        // Core -- collection service
        services.AddSingleton<ICollectionService, FileSystemCollectionService>();

        // ViewModels
        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton<RequestViewModel>();
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