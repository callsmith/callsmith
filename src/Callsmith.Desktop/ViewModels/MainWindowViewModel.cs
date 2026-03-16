using CommunityToolkit.Mvvm.ComponentModel;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Root ViewModel for the main application window.
/// Composes the collections sidebar, environment selector, environment editor,
/// and request/response pane.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public CollectionsViewModel Collections { get; }
    public RequestEditorViewModel RequestEditor { get; }
    public EnvironmentViewModel Environment { get; }
    public EnvironmentEditorViewModel EnvironmentEditor { get; }

    public MainWindowViewModel(
        CollectionsViewModel collections,
        RequestEditorViewModel requestEditor,
        EnvironmentViewModel environment,
        EnvironmentEditorViewModel environmentEditor)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(requestEditor);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(environmentEditor);
        Collections = collections;
        RequestEditor = requestEditor;
        Environment = environment;
        EnvironmentEditor = environmentEditor;
    }
}
