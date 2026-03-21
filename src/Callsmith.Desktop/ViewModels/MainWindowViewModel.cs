using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    public CommandPaletteViewModel CommandPalette { get; }

    public MainWindowViewModel(
        CollectionsViewModel collections,
        RequestEditorViewModel requestEditor,
        EnvironmentViewModel environment,
        EnvironmentEditorViewModel environmentEditor,
        CommandPaletteViewModel commandPalette)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(requestEditor);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(environmentEditor);
        ArgumentNullException.ThrowIfNull(commandPalette);
        Collections = collections;
        RequestEditor = requestEditor;
        Environment = environment;
        EnvironmentEditor = environmentEditor;
        CommandPalette = commandPalette;
    }

    /// <summary>
    /// Ctrl+S handler: saves the current environment editor content when the editor panel is open,
    /// or saves the active request tab otherwise.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (Environment.IsEditorOpen)
            EnvironmentEditor.SaveSelectedCommand.Execute(null);
        else
            RequestEditor.ActiveTab?.SaveCommand.Execute(null);
    }

    /// <summary>
    /// Ctrl+P handler: opens the command palette when the request editor is active
    /// (i.e. the environment editor panel is not open).
    /// </summary>
    [RelayCommand]
    private void OpenCommandPalette()
    {
        if (Environment.IsAnyEditorOpen) return;
        CommandPalette.Open(Collections.TreeRoots);
    }

    /// <summary>
    /// Alt+R handler: reveals the currently active request in the collections sidebar.
    /// </summary>
    [RelayCommand]
    private void RevealActiveRequest()
    {
        var path = RequestEditor.ActiveTab?.SourceFilePath;
        if (string.IsNullOrEmpty(path)) return;
        Collections.RevealFilePath = path;
    }

    /// <summary>
    /// Ctrl+Enter handler: sends the currently active request.
    /// </summary>
    [RelayCommand]
    private void Send()
    {
        if (RequestEditor.ActiveTab != null && 
            !RequestEditor.ActiveTab.IsSending &&
            RequestEditor.ActiveTab.SendCommand.CanExecute(null))
        {
            RequestEditor.ActiveTab?.SendCommand.Execute(null);
        }
    }
}
