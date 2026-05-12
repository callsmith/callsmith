using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Manages the set of open request tabs and acts as the single receiver of
/// cross-cutting messages (request selection, environment changes, collection events).
/// Each tab is an independent <see cref="RequestTabViewModel"/> instance.
/// </summary>
public sealed partial class RequestEditorViewModel : ObservableRecipient,
    IRecipient<RequestSelectedMessage>,
    IRecipient<EnvironmentChangedMessage>,
    IRecipient<GlobalEnvironmentChangedMessage>,
    IRecipient<CollectionItemDeletedMessage>,
    IRecipient<CollectionOpenedMessage>,
    IRecipient<RequestRenamedMessage>,
    IRecipient<RequestSavedMessage>,
    IRecipient<ResendFromHistoryMessage>
{
    private readonly ITransportRegistry _transportRegistry;
    private readonly ICollectionService _collectionService;
    private readonly ICollectionPreferencesService _preferencesService;
    private readonly IAppPreferencesService? _appPreferencesService;
    private readonly IDynamicVariableEvaluator _dynamicEvaluator;
    private readonly IEnvironmentMergeService _mergeService;
    private readonly IHistoryService? _historyService;
    private readonly IEnvironmentService? _environmentService;
    private readonly ILogger<RequestEditorViewModel> _logger;

    private EnvironmentModel? _activeEnvironment;
    private EnvironmentModel _globalEnvironment = new() { FilePath = string.Empty, Name = "Global", Variables = [], EnvironmentId = Guid.NewGuid() };
    private string _collectionPath = string.Empty;
    private bool _appPrefsLoaded;
    private bool _isHorizontalLayout = true;
    private double? _requestEditorHorizontalSplitterFraction;
    private double? _requestEditorVerticalSplitterFraction;

    // -------------------------------------------------------------------------
    // Observable state
    // -------------------------------------------------------------------------

    public ObservableCollection<RequestTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs))]
    private RequestTabViewModel? _activeTab;

    partial void OnActiveTabChanged(RequestTabViewModel? oldValue, RequestTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        Messenger.Send(new ActiveTabChangedMessage(newValue?.SourceFilePath ?? string.Empty));
    }

    [ObservableProperty]
    private IReadOnlyList<string> _availableFolders = [];

    [ObservableProperty]
    private bool _showCloseWithoutSavingDialog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseWithoutSavingPrompt))]
    private string _closeWithoutSavingRequestName = string.Empty;

    private RequestTabViewModel? _pendingCloseTab;
    private readonly Queue<RequestTabViewModel> _pendingBulkCloseTabs = new();
    private RequestTabViewModel? _bulkClosePreferredActiveTab;
    private bool _bulkCloseInProgress;

    /// <summary>
    /// The current transient tab, if any. Set when a request is opened from the sidebar.
    /// Automatically closed when a new sidebar request is selected, unless the tab was promoted.
    /// </summary>
    private RequestTabViewModel? _transientTab;

    private IReadOnlyList<string> _availableRequestNames = [];

    public bool HasTabs => Tabs.Count > 0;

    public string CloseWithoutSavingPrompt =>
        $"Are you sure you want to close \"{CloseWithoutSavingRequestName}\" without saving?";

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public RequestEditorViewModel(
        ITransportRegistry transportRegistry,
        ICollectionService collectionService,
        ICollectionPreferencesService preferencesService,
        IDynamicVariableEvaluator dynamicEvaluator,
        IEnvironmentMergeService mergeService,
        IMessenger messenger,
        ILogger<RequestEditorViewModel> logger,
        IEnvironmentService? environmentService = null,
        IHistoryService? historyService = null,
        IAppPreferencesService? appPreferencesService = null)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(transportRegistry);
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(preferencesService);
        ArgumentNullException.ThrowIfNull(dynamicEvaluator);
        ArgumentNullException.ThrowIfNull(mergeService);
        ArgumentNullException.ThrowIfNull(logger);
        _transportRegistry = transportRegistry;
        _collectionService = collectionService;
        _preferencesService = preferencesService;
        _appPreferencesService = appPreferencesService;
        _dynamicEvaluator = dynamicEvaluator;
        _mergeService = mergeService;
        _historyService = historyService;
        _environmentService = environmentService;
        _logger = logger;
        IsActive = true;

        Tabs.CollectionChanged += OnTabsCollectionChanged;
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    /// <summary>Opens a blank unsaved tab and makes it active.</summary>
    [RelayCommand]
    public void NewTab()
    {
        var tab = BuildTab(request: null);
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>Closes a tab, asking the tab itself to handle its own close guard.</summary>
    [RelayCommand]
    public void CloseTab(RequestTabViewModel tab)
    {
        tab.CloseCommand.Execute(null);
    }

    [RelayCommand]
    private void CancelCloseWithoutSaving()
    {
        _pendingCloseTab = null;
        ShowCloseWithoutSavingDialog = false;
        CloseWithoutSavingRequestName = string.Empty;

        // Canceling any prompt during bulk close aborts remaining close operations.
        if (_bulkCloseInProgress)
            EndBulkClose(aborted: true);
    }

    [RelayCommand]
    private void ConfirmCloseWithoutSaving()
    {
        var tab = _pendingCloseTab;
        _pendingCloseTab = null;
        ShowCloseWithoutSavingDialog = false;
        CloseWithoutSavingRequestName = string.Empty;

        if (tab is null || !Tabs.Contains(tab))
        {
            if (_bulkCloseInProgress)
                ProcessNextBulkClose();
            return;
        }

        if (tab.DiscardAndCloseCommand.CanExecute(null))
            tab.DiscardAndCloseCommand.Execute(null);

        if (_bulkCloseInProgress)
            ProcessNextBulkClose();
    }

    [RelayCommand]
    private async Task SaveAndClosePendingTabAsync(CancellationToken ct)
    {
        var tab = _pendingCloseTab;
        _pendingCloseTab = null;
        ShowCloseWithoutSavingDialog = false;
        CloseWithoutSavingRequestName = string.Empty;

        if (tab is null || !Tabs.Contains(tab))
        {
            if (_bulkCloseInProgress)
                ProcessNextBulkClose();
            return;
        }

        // Save As is tab-local UI for new tabs, so ensure it is visible when chosen.
        if (tab.IsNew && ActiveTab != tab)
            ActiveTab = tab;

        if (tab.SaveAndCloseCommand.CanExecute(null))
            await tab.SaveAndCloseCommand.ExecuteAsync(ct);

        if (_bulkCloseInProgress)
        {
            // Continue only when this tab actually closed; otherwise stop the batch.
            if (!Tabs.Contains(tab))
                ProcessNextBulkClose();
            else
                EndBulkClose(aborted: true);
        }
    }

    /// <summary>Closes all tabs except the specified one.</summary>
    public void CloseOtherTabs(RequestTabViewModel keep)
    {
        var toClose = Tabs.Where(t => t != keep).ToList();
        StartBulkClose(toClose, keep);
    }

    /// <summary>Closes all tabs to the right of the specified tab (higher indices).</summary>
    public void CloseTabsToTheRight(RequestTabViewModel pivot)
    {
        var idx = Tabs.IndexOf(pivot);
        if (idx < 0) return;
        var toClose = Tabs.Skip(idx + 1).ToList();
        StartBulkClose(toClose, pivot);
    }

    /// <summary>Closes all tabs whose requests have been saved to disk (non-new, non-dirty).</summary>
    public void CloseSavedTabs()
    {
        var toClose = Tabs.Where(t => !t.IsNew && !t.TabIsDirty).ToList();
        foreach (var tab in toClose)
            RemoveTab(tab);
    }

    /// <summary>Closes all open tabs.</summary>
    public void CloseAllTabs()
    {
        var toClose = Tabs.ToList();
        StartBulkClose(toClose, preferredActiveTab: null);
    }

    /// <summary>Makes the specified tab active (called by clicking a tab chip).</summary>
    [RelayCommand]
    public void SelectTab(RequestTabViewModel tab)
    {
        ActiveTab = tab;
    }

    /// <summary>Moves a tab from <paramref name="fromIndex"/> to <paramref name="toIndex"/> for drag reordering.</summary>
    public void MoveTab(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Tabs.Count) return;
        if (toIndex < 0 || toIndex >= Tabs.Count) return;
        if (fromIndex == toIndex) return;

        var tab = Tabs[fromIndex];
        Tabs.RemoveAt(fromIndex);
        Tabs.Insert(toIndex, tab);
    }

    // -------------------------------------------------------------------------
    // Message receivers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Opens the selected request in a tab. When <see cref="RequestSelectedMessage.OpenAsPermanent"/>
    /// is <see langword="false"/> (the default, used for sidebar clicks) the tab is transient and replaces
    /// any previous transient tab. When <see langword="true"/> (used by the command palette) the tab is
    /// opened as a permanent tab and the existing transient tab is left untouched.
    /// If the request is already open in any tab, that tab is focused instead.
    /// </summary>
    public void Receive(RequestSelectedMessage message)
    {
        var incoming = message.Value;

        // If this request is already open in any tab (transient or permanent), just focus it.
        var existing = Tabs.FirstOrDefault(t =>
            !t.IsNew &&
            string.Equals(t.SourceFilePath, incoming.FilePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (message.OpenAsPermanent && existing.IsTransient)
            {
                existing.PromoteFromTransient();
                if (ReferenceEquals(_transientTab, existing))
                    _transientTab = null;
            }

            ActiveTab = existing;
            return;
        }

        if (message.OpenAsPermanent)
        {
            // Command-palette selection: open as a permanent (non-transient) tab.
            // Do not displace the existing transient tab — they are independent.
            var tab = BuildTab(incoming);
            Tabs.Add(tab);
            ActiveTab = tab;
            return;
        }

        // Close the previous transient tab if it has not been promoted to a permanent tab.
        // Tabs.Contains check is a defensive guard: the tab may have already been closed by
        // other code paths (e.g., the underlying file being deleted). IsTransient ensures we
        // only auto-close tabs the user has not intentionally promoted via edit or double-click.
        if (_transientTab is not null && Tabs.Contains(_transientTab) && _transientTab.IsTransient)
            RemoveTab(_transientTab);
        _transientTab = null;

        var newTab = BuildTab(incoming);
        newTab.IsTransient = true;
        _transientTab = newTab;
        Tabs.Add(newTab);
        ActiveTab = newTab;
    }

    public void Receive(EnvironmentChangedMessage message)
    {
        _activeEnvironment = message.Value;
        foreach (var tab in Tabs)
            tab.SetEnvironment(message.Value);
    }

    public void Receive(GlobalEnvironmentChangedMessage message)
    {
        _globalEnvironment = message.Value;
        foreach (var tab in Tabs)
            tab.SetGlobalEnvironment(message.Value);
    }

    public void Receive(CollectionItemDeletedMessage message)
    {
        var hint = message.Value;
        var toClose = Tabs
            .Where(t => !string.IsNullOrEmpty(t.SourceFilePath) &&
                        (string.Equals(t.SourceFilePath, hint, StringComparison.OrdinalIgnoreCase) ||
                         t.SourceFilePath.StartsWith(hint, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var tab in toClose)
            RemoveTab(tab);
    }

    public void Receive(CollectionOpenedMessage message)
    {
        // Clear tabs from the previous collection.
        Tabs.Clear();
        ActiveTab = null;

        _collectionPath = message.Value;
        _ = UpdateAvailableFoldersAsync(message.Value);
        _ = RestoreTabsAsync(message.Value);
    }

    /// <summary>
    /// Called when a request is renamed. Finds any open tab for that request and updates
    /// its _sourceRequest to reflect the new name and file path. This ensures:
    /// - Tab title reflects the new name
    /// - Session persistence stores the new file path
    /// - Dynamic variable cache keys use the current request name
    /// </summary>
    public void Receive(RequestRenamedMessage message)
    {
        var tab = Tabs.FirstOrDefault(t =>
            !string.IsNullOrEmpty(t.SourceFilePath) &&
            string.Equals(t.SourceFilePath, message.OldFilePath, StringComparison.OrdinalIgnoreCase));

        if (tab is not null)
        {
            tab.UpdateSourceRequest(message.Renamed);
        }
    }

    /// <summary>
    /// Called when a request is saved. Persists the current tab state to ensure
    /// newly saved tabs are included in session restoration.
    /// </summary>
    public void Receive(RequestSavedMessage message)
    {
    }

    /// <summary>
    /// Opens a new unsaved tab pre-populated with the fully-resolved values from a history entry.
    /// </summary>
    public void Receive(ResendFromHistoryMessage message)
    {
        var tab = BuildTab(request: null);
        tab.LoadFromHistorySnapshot(
            message.Entry.ConfiguredSnapshot,
            message.Entry.VariableBindings);
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    // -------------------------------------------------------------------------
    // Internal: called as the close callback injected into each tab
    // -------------------------------------------------------------------------

    internal void RemoveTab(RequestTabViewModel tab)
    {
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (_transientTab == tab)
            _transientTab = null;

        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count > 0
                ? Tabs[Math.Max(0, idx - 1)]
                : null;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private RequestTabViewModel BuildTab(CollectionRequest? request)
    {
        var tab = new RequestTabViewModel(
            _transportRegistry,
            _collectionService,
            Messenger,
            RemoveTab,
            _dynamicEvaluator,
            _historyService,
            _environmentService,
            _mergeService);

        tab.SetGlobalCloseGuardHandler(ShowGlobalCloseWithoutSavingDialog);

        tab.AvailableFolders = AvailableFolders;
        tab.CollectionRootPath = _collectionPath;
        tab.AvailableRequestNames = _availableRequestNames;
        tab.SaveAsFolderPath = string.Empty;
        tab.SetEnvironment(_activeEnvironment);
        tab.SetGlobalEnvironment(_globalEnvironment);
        tab.IsHorizontalLayout = _isHorizontalLayout;
        tab.HorizontalSplitterPosition = _requestEditorHorizontalSplitterFraction;
        tab.VerticalSplitterPosition = _requestEditorVerticalSplitterFraction;

        tab.LayoutChangedCallback = isHorizontal =>
        {
            _isHorizontalLayout = isHorizontal;
            // Sync all other tabs to the new layout instantly.
            foreach (var other in Tabs.Where(t => t != tab))
                other.IsHorizontalLayout = isHorizontal;
            _ = PersistLayoutAsync();
        };

        tab.SplitterChangedCallback = (fraction, isHorizontal) =>
        {
            if (isHorizontal)
                _requestEditorHorizontalSplitterFraction = fraction;
            else
                _requestEditorVerticalSplitterFraction = fraction;
            // Sync all tabs (including this one) so the fraction is always current
            // and restores correctly when the user toggles orientation.
            foreach (var t in Tabs)
            {
                if (isHorizontal)
                    t.HorizontalSplitterPosition = fraction;
                else
                    t.VerticalSplitterPosition = fraction;
            }
            _ = PersistSplitterPositionAsync();
        };

        if (request is not null)
            tab.LoadRequest(request);
        else
            tab.IsNew = true;

        return tab;
    }

    internal Task PersistSessionAsync(CancellationToken ct = default)
    {
        return PersistTabsAsync(ct);
    }

    private void ShowGlobalCloseWithoutSavingDialog(RequestTabViewModel tab)
    {
        _pendingCloseTab = tab;
        CloseWithoutSavingRequestName = string.IsNullOrWhiteSpace(tab.TabTitle) ? "New Request" : tab.TabTitle;
        ShowCloseWithoutSavingDialog = true;
    }

    private void StartBulkClose(IEnumerable<RequestTabViewModel> tabsToClose, RequestTabViewModel? preferredActiveTab)
    {
        _pendingBulkCloseTabs.Clear();
        foreach (var tab in tabsToClose)
            _pendingBulkCloseTabs.Enqueue(tab);

        _bulkClosePreferredActiveTab = preferredActiveTab;
        _bulkCloseInProgress = true;

        ProcessNextBulkClose();
    }

    private void ProcessNextBulkClose()
    {
        if (!_bulkCloseInProgress)
            return;

        while (_pendingBulkCloseTabs.Count > 0)
        {
            var tab = _pendingBulkCloseTabs.Dequeue();

            if (!Tabs.Contains(tab))
                continue;

            if (tab.TabIsDirty)
            {
                // For bulk close actions, focus each dirty tab as it's being confirmed.
                ActiveTab = tab;
                ShowGlobalCloseWithoutSavingDialog(tab);
                return;
            }

            RemoveTab(tab);
        }

        EndBulkClose(aborted: false);
    }

    private void EndBulkClose(bool aborted)
    {
        _bulkCloseInProgress = false;
        _pendingBulkCloseTabs.Clear();

        var preferred = _bulkClosePreferredActiveTab;
        _bulkClosePreferredActiveTab = null;

        if (aborted)
            return;

        if (preferred is not null && Tabs.Contains(preferred))
            ActiveTab = preferred;
    }

    private async Task PersistLayoutAsync()
    {
        if (_appPreferencesService is null) return;
        var newValue = _isHorizontalLayout;
        await _appPreferencesService.UpdateAsync(
            p => p with { IsHorizontalRequestEditorLayout = newValue }).ConfigureAwait(false);
    }

    private async Task PersistSplitterPositionAsync()
    {
        if (_appPreferencesService is null) return;
        var h = _requestEditorHorizontalSplitterFraction;
        var v = _requestEditorVerticalSplitterFraction;
        await _appPreferencesService.UpdateAsync(
            p => p with
            {
                RequestEditorHorizontalSplitterFraction = h,
                RequestEditorVerticalSplitterFraction = v
            }).ConfigureAwait(false);
    }

    private async Task UpdateAvailableFoldersAsync(string collectionPath)
    {
        try
        {
            var root = await _collectionService.OpenFolderAsync(collectionPath);
            var folders = new List<string>();
            var requestNames = new List<string>();
            CollectFolderPaths(root, collectionPath, folders);
            CollectRequestNames(root, requestNames);
            AvailableFolders = folders.AsReadOnly();
            _availableRequestNames = requestNames.AsReadOnly();

            // Push updated list to any already-open new tabs.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var tab in Tabs.Where(t => t.IsNew))
                {
                    tab.AvailableFolders = AvailableFolders;
                    tab.CollectionRootPath = collectionPath;
                    tab.AvailableRequestNames = _availableRequestNames;
                    if (string.IsNullOrEmpty(tab.SaveAsFolderPath))
                        tab.SaveAsFolderPath = string.Empty;
                }
            });
        }
        catch
        {
            // Non-critical: folder enumeration is best-effort
        }
    }

    private static void CollectFolderPaths(CollectionFolder folder, string rootPath, List<string> accumulator)
    {
        // Use empty string for the collection root; relative path for all subfolders.
        var relative = Path.GetRelativePath(rootPath, folder.FolderPath);
        accumulator.Add(relative == "." ? string.Empty : relative);
        foreach (var sub in folder.SubFolders)
            CollectFolderPaths(sub, rootPath, accumulator);
    }

    private static void CollectRequestNames(CollectionFolder folder, List<string> accumulator)
    {
        foreach (var req in folder.Requests)
            if (!string.IsNullOrWhiteSpace(req.Name))
                accumulator.Add(req.Name);
        foreach (var sub in folder.SubFolders)
            CollectRequestNames(sub, accumulator);
    }

    private async Task RestoreTabsAsync(string collectionPath)
    {
        try
        {
            // Load prefs and all referenced request files off the UI thread.
            var (prefs, appPrefs, tabsToRestore) = await Task.Run(async () =>
            {
                var p = await _preferencesService.LoadAsync(collectionPath).ConfigureAwait(false);
                var ap = _appPreferencesService is not null
                    ? await _appPreferencesService.LoadAsync().ConfigureAwait(false)
                    : null;
                var tabs = new List<(CollectionRequest? sourceRequest, CollectionRequest? draftRequest)>();

                if (p.OpenTabs is { Count: > 0 })
                {
                    foreach (var openTab in p.OpenTabs)
                    {
                        CollectionRequest? sourceRequest = null;

                        if (!string.IsNullOrEmpty(openTab.SourceFilePath))
                        {
                            var absPath = Path.GetFullPath(Path.Combine(collectionPath, openTab.SourceFilePath));
                            if (File.Exists(absPath))
                            {
                                try
                                {
                                    sourceRequest = await _collectionService.LoadRequestAsync(absPath).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Could not restore tab source for '{Path}'", absPath);
                                }
                            }
                        }

                        tabs.Add((sourceRequest, openTab.DraftRequest));
                    }
                }

                return (p, ap, tabs);
            }).ConfigureAwait(true); // resume on UI thread to manipulate Tabs

            if (!_appPrefsLoaded)
            {
                _appPrefsLoaded = true;
                _isHorizontalLayout = appPrefs?.IsHorizontalRequestEditorLayout ?? true;
                _requestEditorHorizontalSplitterFraction = appPrefs?.RequestEditorHorizontalSplitterFraction;
                _requestEditorVerticalSplitterFraction = appPrefs?.RequestEditorVerticalSplitterFraction;
            }

            RequestTabViewModel? tabToActivate = null;

            for (var index = 0; index < tabsToRestore.Count; index++)
            {
                var (sourceRequest, draftRequest) = tabsToRestore[index];
                RequestTabViewModel? tab = null;

                if (draftRequest is not null)
                {
                    tab = BuildTab(request: null);
                    tab.RestorePersistedState(draftRequest, sourceRequest);
                }
                else if (sourceRequest is not null)
                {
                    tab = BuildTab(sourceRequest);
                }

                if (tab is null)
                    continue;

                Tabs.Add(tab);

                if (prefs.ActiveTabIndex is int activeTabIndex &&
                    activeTabIndex == index)
                {
                    tabToActivate = tab;
                }
            }

            ActiveTab = tabToActivate ?? Tabs.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore tabs for '{Path}'", collectionPath);
        }
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTabs));
    }

    private async Task PersistTabsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_collectionPath)) return;
        try
        {
            var collectionPath = _collectionPath;

            var openTabs = Tabs
                .Select(tab =>
                {
                    var sourcePath = string.IsNullOrEmpty(tab.SourceFilePath)
                        ? null
                        : Path.GetRelativePath(collectionPath, tab.SourceFilePath);

                    return !tab.IsNew && !tab.HasUnsavedChanges
                        ? new OpenRequestTabState
                        {
                            SourceFilePath = sourcePath
                        }
                        : new OpenRequestTabState
                        {
                            SourceFilePath = sourcePath,
                            DraftRequest = tab.CreateSessionSnapshot()
                        };
                })
                .ToList();

            var activeTabIndex = ActiveTab is null ? (int?)null : Tabs.IndexOf(ActiveTab);

            await _preferencesService.UpdateAsync(collectionPath, current => new()
            {
                LastActiveEnvironmentFile = current.LastActiveEnvironmentFile,
                OpenTabs = openTabs.Count > 0 ? openTabs.AsReadOnly() : null,
                ActiveTabIndex = activeTabIndex is >= 0 ? activeTabIndex : null,
                ExpandedFolderPaths = current.ExpandedFolderPaths,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist open tabs for '{Path}'", _collectionPath);
        }
    }
}
