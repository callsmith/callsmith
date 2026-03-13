using CommunityToolkit.Mvvm.ComponentModel;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Root ViewModel for the main application window.
/// Composes the collections sidebar, environment selector, and request/response pane.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public CollectionsViewModel Collections { get; }
    public RequestViewModel Request { get; }
    public EnvironmentViewModel Environment { get; }

    public MainWindowViewModel(
        CollectionsViewModel collections,
        RequestViewModel request,
        EnvironmentViewModel environment)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        Collections = collections;
        Request = request;
        Environment = environment;
    }
}
