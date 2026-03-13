using CommunityToolkit.Mvvm.ComponentModel;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Root ViewModel for the main application window.
/// Composes the collections sidebar and the request/response pane.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public CollectionsViewModel Collections { get; }
    public RequestViewModel Request { get; }

    public MainWindowViewModel(CollectionsViewModel collections, RequestViewModel request)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(request);
        Collections = collections;
        Request = request;
    }
}
