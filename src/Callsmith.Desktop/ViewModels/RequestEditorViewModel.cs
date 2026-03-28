using System.Collections.ObjectModel;
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
    private readonly IDynamicVariableEvaluator _dynamicEvaluator;
    private readonly IEnvironmentMergeService _mergeService;
    private readonly IHistoryService? _historyService;
    private readonly IEnvironmentService? _environmentService;
    private readonly ILogger<RequestEditorViewModel> _logger;

    private EnvironmentModel? _activeEnvironment;
    private EnvironmentModel _globalEnvironment = new() { FilePath = string.Empty, Name = "Global", Variables = [], EnvironmentId = Guid.NewGuid() };
    private string _collectionPath = string.Empty;
    private bool _restoringTabs;
    private bool _isHorizontalLayout = true;

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
        if (!_restoringTabs)
            _ = PersistTabsAsync();
    }

    [ObservableProperty]
    private IReadOnlyList<string> _availableFolders = [];

    private IReadOnlyList<string> _availableRequestNames = [];

    public bool HasTabs => Tabs.Count > 0;

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
        IHistoryService? historyService = null)
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
        _dynamicEvaluator = dynamicEvaluator;
        _mergeService = mergeService;
        _historyService = historyService;
        _environmentService = environmentService;
        _logger = logger;
        IsActive = true;

        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            if (!_restoringTabs)
                _ = PersistTabsAsync();
        };
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

    /// <summary>Closes all tabs except the specified one.</summary>
    public void CloseOtherTabs(RequestTabViewModel keep)
    {
        var toClose = Tabs.Where(t => t != keep).ToList();
        foreach (var tab in toClose)
            RemoveTab(tab);
        ActiveTab = keep;
    }

    /// <summary>Closes all tabs to the right of the specified tab (higher indices).</summary>
    public void CloseTabsToTheRight(RequestTabViewModel pivot)
    {
        var idx = Tabs.IndexOf(pivot);
        if (idx < 0) return;
        var toClose = Tabs.Skip(idx + 1).ToList();
        foreach (var tab in toClose)
            RemoveTab(tab);
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
        foreach (var tab in toClose)
            RemoveTab(tab);
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
    /// Opens the selected request in a new tab, or focuses it if already open.
    /// </summary>
    public void Receive(RequestSelectedMessage message)
    {
        var incoming = message.Value;

        // If this request is already open in a tab, just focus that tab.
        var existing = Tabs.FirstOrDefault(t =>
            !t.IsNew &&
            string.Equals(t.SourceFilePath, incoming.FilePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        var tab = BuildTab(incoming);
        Tabs.Add(tab);
        ActiveTab = tab;
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
        // Clear tabs from the previous collection without triggering persistence.
        _restoringTabs = true;
        Tabs.Clear();
        ActiveTab = null;
        _restoringTabs = false;

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
            _ = PersistTabsAsync();
        }
    }

    /// <summary>
    /// Called when a request is saved. Persists the current tab state to ensure
    /// newly saved tabs are included in session restoration.
    /// </summary>
    public void Receive(RequestSavedMessage message)
    {
        _ = PersistTabsAsync();
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

        tab.AvailableFolders = AvailableFolders;
        tab.CollectionRootPath = _collectionPath;
        tab.AvailableRequestNames = _availableRequestNames;
        tab.SaveAsFolderPath = string.Empty;
        tab.SetEnvironment(_activeEnvironment);
        tab.SetGlobalEnvironment(_globalEnvironment);
        tab.IsHorizontalLayout = _isHorizontalLayout;

        tab.LayoutChangedCallback = isHorizontal =>
        {
            _isHorizontalLayout = isHorizontal;
            // Sync all other tabs to the new layout instantly.
            foreach (var other in Tabs.Where(t => t != tab))
                other.IsHorizontalLayout = isHorizontal;
            _ = PersistLayoutAsync();
        };

        if (request is not null)
            tab.LoadRequest(request);
        else
            tab.IsNew = true;

        return tab;
    }

    private async Task PersistLayoutAsync()
    {
        if (string.IsNullOrEmpty(_collectionPath)) return;
        try
        {
            await _preferencesService.UpdateAsync(_collectionPath, current => new()
            {
                LastActiveEnvironmentFile = current.LastActiveEnvironmentFile,
                OpenTabPaths = current.OpenTabPaths,
                ActiveTabPath = current.ActiveTabPath,
                ExpandedFolderPaths = current.ExpandedFolderPaths,
                IsHorizontalLayout = _isHorizontalLayout,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist layout preference for '{Path}'", _collectionPath);
        }
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
            var (prefs, requests) = await Task.Run(async () =>
            {
                var p = await _preferencesService.LoadAsync(collectionPath).ConfigureAwait(false);
                var reqs = new List<(string relPath, CollectionRequest req)>();

                if (p.OpenTabPaths is { Count: > 0 })
                {
                    foreach (var relPath in p.OpenTabPaths)
                    {
                        var absPath = Path.GetFullPath(Path.Combine(collectionPath, relPath));
                        if (!File.Exists(absPath)) continue;
                        try
                        {
                            var req = await _collectionService.LoadRequestAsync(absPath).ConfigureAwait(false);
                            reqs.Add((relPath, req));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not restore tab for '{Path}'", absPath);
                        }
                    }
                }

                return (p, reqs);
            }).ConfigureAwait(true); // resume on UI thread to manipulate Tabs

            _restoringTabs = true;
            try
            {
                _isHorizontalLayout = prefs.IsHorizontalLayout;
                RequestTabViewModel? tabToActivate = null;

                foreach (var (relPath, request) in requests)
                {
                    var tab = BuildTab(request);
                    Tabs.Add(tab);

                    if (prefs.ActiveTabPath is not null &&
                        string.Equals(relPath, prefs.ActiveTabPath, StringComparison.OrdinalIgnoreCase))
                    {
                        tabToActivate = tab;
                    }
                }

                ActiveTab = tabToActivate ?? Tabs.FirstOrDefault();
            }
            finally
            {
                _restoringTabs = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore tabs for '{Path}'", collectionPath);
        }
    }

    private async Task PersistTabsAsync()
    {
        if (string.IsNullOrEmpty(_collectionPath)) return;
        try
        {
            var collectionPath = _collectionPath;

            var tabPaths = Tabs
                .Where(t => !t.IsNew && !string.IsNullOrEmpty(t.SourceFilePath))
                .Select(t => Path.GetRelativePath(collectionPath, t.SourceFilePath))
                .ToList();

            var activePath = ActiveTab is { IsNew: false } active && !string.IsNullOrEmpty(active.SourceFilePath)
                ? Path.GetRelativePath(collectionPath, active.SourceFilePath)
                : null;

            await _preferencesService.UpdateAsync(collectionPath, current => new()
            {
                LastActiveEnvironmentFile = current.LastActiveEnvironmentFile,
                OpenTabPaths = tabPaths.Count > 0 ? tabPaths.AsReadOnly() : null,
                ActiveTabPath = activePath,
                ExpandedFolderPaths = current.ExpandedFolderPaths,
                IsHorizontalLayout = _isHorizontalLayout,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist open tabs for '{Path}'", _collectionPath);
        }
    }
}
