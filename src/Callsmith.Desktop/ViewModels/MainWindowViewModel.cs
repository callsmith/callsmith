using Callsmith.Core.Abstractions;
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
    private readonly IAppPreferencesService? _appPreferencesService;

    public CollectionsViewModel Collections { get; }
    public RequestEditorViewModel RequestEditor { get; }
    public EnvironmentViewModel Environment { get; }
    public EnvironmentEditorViewModel EnvironmentEditor { get; }
    public CommandPaletteViewModel CommandPalette { get; }
    public HistoryPanelViewModel HistoryPanel { get; }

    /// <summary>
    /// True when the right pane (request editor + response viewer) should be visible.
    /// This is when a collection is open AND no editor panel is open.
    /// </summary>
    public bool IsRightPaneVisible => Collections.HasCollection && !Environment.IsAnyEditorOpen;

    /// <summary>
    /// True when the placeholder (no collection) message should be visible.
    /// This is when no collection is open AND no editor panel is open.
    /// </summary>
    public bool IsPlaceholderVisible => !Collections.HasCollection && !Environment.IsAnyEditorOpen;

    /// <summary>
    /// True when the left sidebar should be visible in its normal narrow column.
    /// This is when a collection is open AND no editor panel is open.
    /// </summary>
    public bool IsSidebarVisible => Collections.HasCollection && !Environment.IsAnyEditorOpen;

    /// <summary>
    /// Saved fraction (0.0–1.0) of the total width allocated to the left sidebar column.
    /// Null means the default is used.
    /// Loaded asynchronously when the app starts; exposed for the view to apply and update.
    /// </summary>
    internal double? RequestTreeSplitterFraction { get; private set; }

    public MainWindowViewModel(
        CollectionsViewModel collections,
        RequestEditorViewModel requestEditor,
        EnvironmentViewModel environment,
        EnvironmentEditorViewModel environmentEditor,
        CommandPaletteViewModel commandPalette,
        HistoryPanelViewModel historyPanel,
        IMessenger messenger,
        IAppPreferencesService? appPreferencesService = null)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(requestEditor);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(environmentEditor);
        ArgumentNullException.ThrowIfNull(commandPalette);
        ArgumentNullException.ThrowIfNull(historyPanel);
        ArgumentNullException.ThrowIfNull(messenger);
        _messenger = messenger;
        _appPreferencesService = appPreferencesService;
        Collections = collections;
        RequestEditor = requestEditor;
        Environment = environment;
        EnvironmentEditor = environmentEditor;
        CommandPalette = commandPalette;
        HistoryPanel = historyPanel;

        // Subscribe to property changes that affect computed properties
        Collections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Collections.HasCollection))
            {
                OnPropertyChanged(nameof(IsRightPaneVisible));
                OnPropertyChanged(nameof(IsPlaceholderVisible));
                OnPropertyChanged(nameof(IsSidebarVisible));
            }
        };

        Environment.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Environment.IsAnyEditorOpen))
            {
                OnPropertyChanged(nameof(IsRightPaneVisible));
                OnPropertyChanged(nameof(IsPlaceholderVisible));
                OnPropertyChanged(nameof(IsSidebarVisible));
            }
        };

        _messenger.Register<MainWindowViewModel, OpenHistoryMessage>(this, static (recipient, message) =>
        {
            if (message.RequestId is { } requestId)
                recipient.HistoryPanel.OpenForRequest(requestId, message.RequestName);
            else
                recipient.HistoryPanel.OpenGlobal();
        });

        Collections.TriggerStartupLoad();

        if (_appPreferencesService is not null)
            _ = LoadPreferencesAsync();
    }

    private async Task LoadPreferencesAsync()
    {
        var prefs = await _appPreferencesService!.LoadAsync().ConfigureAwait(false);
        RequestTreeSplitterFraction = prefs.RequestTreeSplitterFraction;
        OnPropertyChanged(nameof(RequestTreeSplitterFraction));
    }

    /// <summary>
    /// Called by the view when the user finishes dragging the sidebar splitter.
    /// Persists the new fraction so it can be restored on next launch.
    /// </summary>
    internal void OnRequestTreeSplitterMoved(double fraction)
    {
        RequestTreeSplitterFraction = fraction;
        if (_appPreferencesService is not null)
            _ = _appPreferencesService.UpdateAsync(p => p with { RequestTreeSplitterFraction = fraction });
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
    /// Ctrl+P handler: opens the command palette only when the main request editor is active
    /// (i.e. neither the environment editor panel nor the history panel is open).
    /// </summary>
    [RelayCommand]
    private void OpenCommandPalette()
    {
        if (Environment.IsAnyEditorOpen) return;
        if (HistoryPanel.IsOpen) return;
        CommandPalette.Open(Collections.TreeRoots);
    }

    /// <summary>
    /// Ctrl+T handler: opens a new request tab only when the main request editor is active.
    /// </summary>
    [RelayCommand]
    private void NewTab()
    {
        if (Environment.IsAnyEditorOpen) return;
        if (HistoryPanel.IsOpen) return;
        if (!Collections.HasCollection) return;

        RequestEditor.NewTabCommand.Execute(null);
    }

    /// <summary>
    /// Ctrl+W handler: closes the currently active request tab when the main request editor is active.
    /// </summary>
    [RelayCommand]
    private void CloseCurrentTab()
    {
        if (Environment.IsAnyEditorOpen) return;
        if (HistoryPanel.IsOpen) return;
        if (!Collections.HasCollection) return;

        if (RequestEditor.ActiveTab?.CloseCommand.CanExecute(null) == true)
            RequestEditor.ActiveTab.CloseCommand.Execute(null);
    }

    /// <summary>
    /// Ctrl+E handler: opens the environment configuration screen from the main editor view.
    /// </summary>
    [RelayCommand]
    private void OpenEnvironmentConfiguration()
    {
        if (!Collections.HasCollection) return;
        if (HistoryPanel.IsOpen) return;
        Environment.OpenEditorCommand.Execute(null);
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
