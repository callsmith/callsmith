using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Root ViewModel for the main application window.
/// Composes the collections sidebar, environment selector, environment editor,
/// and request/response pane.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IMessenger _messenger;

    public CollectionsViewModel Collections { get; }
    public RequestEditorViewModel RequestEditor { get; }
    public EnvironmentViewModel Environment { get; }
    public EnvironmentEditorViewModel EnvironmentEditor { get; }
    public CommandPaletteViewModel CommandPalette { get; }
    public HistoryPanelViewModel HistoryPanel { get; }

    public MainWindowViewModel(
        CollectionsViewModel collections,
        RequestEditorViewModel requestEditor,
        EnvironmentViewModel environment,
        EnvironmentEditorViewModel environmentEditor,
        CommandPaletteViewModel commandPalette,
        HistoryPanelViewModel historyPanel,
        IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(requestEditor);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(environmentEditor);
        ArgumentNullException.ThrowIfNull(commandPalette);
        ArgumentNullException.ThrowIfNull(historyPanel);
        ArgumentNullException.ThrowIfNull(messenger);
        _messenger = messenger;
        Collections = collections;
        RequestEditor = requestEditor;
        Environment = environment;
        EnvironmentEditor = environmentEditor;
        CommandPalette = commandPalette;
        HistoryPanel = historyPanel;

        _messenger.Register<MainWindowViewModel, OpenHistoryMessage>(this, static (recipient, message) =>
        {
            if (message.RequestId is { } requestId)
                recipient.HistoryPanel.OpenForRequest(requestId, message.RequestName);
            else
                recipient.HistoryPanel.OpenGlobal();
        });
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

    [RelayCommand]
    private void OpenActiveRequestHistory()
    {
        var activeTab = RequestEditor.ActiveTab;
        if (activeTab is null || !activeTab.CanOpenRequestHistory)
            return;

        if (activeTab.OpenRequestHistoryCommand.CanExecute(null))
            activeTab.OpenRequestHistoryCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenGlobalHistory() => HistoryPanel.OpenGlobal();

    [RelayCommand]
    private void OpenHistory() => OpenGlobalHistory();

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
