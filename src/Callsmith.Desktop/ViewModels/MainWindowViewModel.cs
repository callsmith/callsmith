using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Actions;
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
    private readonly IUndoRedoService? _undoRedoService;

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
        IAppPreferencesService? appPreferencesService = null,
        IUndoRedoService? undoRedoService = null)
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
        _undoRedoService = undoRedoService;
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

        // Clear the undo/redo stack when a new collection is opened.
        _messenger.Register<MainWindowViewModel, CollectionOpenedMessage>(this,
            static (recipient, _) => recipient._undoRedoService?.Clear());

        // Refresh undo/redo command CanExecute whenever the stacks change.
        if (_undoRedoService is not null)
        {
            _undoRedoService.StackChanged += (_, _) =>
            {
                UndoCommand.NotifyCanExecuteChanged();
                RedoCommand.NotifyCanExecuteChanged();
            };
        }

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

    [RelayCommand]
    private void CollapseAllFolders()
    {
        if (!Collections.HasCollection) return;
        if (Environment.IsAnyEditorOpen) return;
        if (HistoryPanel.IsOpen) return;

        if (Collections.CollapseAllFoldersCommand.CanExecute(null))
            Collections.CollapseAllFoldersCommand.Execute(null);
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

    // ── Undo / Redo ───────────────────────────────────────────────────────────

    /// <summary>Ctrl+Z handler: undoes the most recent tracked edit.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => PerformUndoRedo(undo: true);

    /// <summary>Ctrl+Y / Ctrl+Shift+Z handler: redoes the most recently undone edit.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => PerformUndoRedo(undo: false);

    private bool CanUndo() => _undoRedoService?.CanUndo == true;
    private bool CanRedo() => _undoRedoService?.CanRedo == true;

    private void PerformUndoRedo(bool undo)
    {
        if (_undoRedoService is null)
            return;

        var action = undo ? _undoRedoService.Undo() : _undoRedoService.Redo();
        if (action is null)
            return;

        if (action is RequestTabMementoAction requestAction)
            ApplyRequestTabAction(requestAction, undo);
        else if (action is EnvironmentMementoAction envAction)
            ApplyEnvironmentAction(envAction, undo);
    }

    private void ApplyRequestTabAction(RequestTabMementoAction action, bool undo)
    {
        var snapshot = undo ? action.Before : action.After;

        // Find the tab by file path (TabId may differ if the tab was re-opened).
        var tab = RequestEditor.Tabs.FirstOrDefault(t =>
            !string.IsNullOrEmpty(action.FilePath) &&
            string.Equals(t.SourceFilePath, action.FilePath, StringComparison.OrdinalIgnoreCase));

        if (tab is not null)
        {
            // Flush any pending debounce on the open tab before overwriting its state.
            tab.FlushUndoDebounce();
            RequestEditor.ActiveTab = tab;
        }
        else
        {
            // Tab is not open; re-open it via the standard navigation path.
            _messenger.Send(new RequestSelectedMessage(snapshot, openAsPermanent: true));
            // After the synchronous message dispatch, the new tab is the active tab.
            tab = RequestEditor.ActiveTab;
        }

        // Close the environment editor so the request editor is visible.
        if (Environment.IsEditorOpen)
            _messenger.Send(new CloseEnvironmentEditorMessage());

        tab?.ApplySnapshot(snapshot);
    }

    private void ApplyEnvironmentAction(EnvironmentMementoAction action, bool undo)
    {
        var snapshot = undo ? action.Before : action.After;

        var envVm = EnvironmentEditor.Environments
            .FirstOrDefault(e => e.EnvironmentId == action.EnvironmentId);

        if (envVm is null)
            return;

        // Flush any pending debounce before overwriting.
        envVm.FlushUndoDebounce();

        // Open the environment editor panel if it is not already visible.
        if (!Environment.IsEditorOpen)
            Environment.OpenEditorCommand.Execute(null);

        // Select the specific environment row.
        EnvironmentEditor.SelectEnvironmentById(action.EnvironmentId);

        envVm.ApplySnapshot(snapshot);
    }
}
