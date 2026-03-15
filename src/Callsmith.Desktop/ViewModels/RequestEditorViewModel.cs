using System.Collections.ObjectModel;
using Avalonia.Threading;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Manages the set of open request tabs and acts as the single receiver of
/// cross-cutting messages (request selection, environment changes, collection events).
/// Each tab is an independent <see cref="RequestTabViewModel"/> instance.
/// </summary>
public sealed partial class RequestEditorViewModel : ObservableRecipient,
    IRecipient<RequestSelectedMessage>,
    IRecipient<EnvironmentChangedMessage>,
    IRecipient<CollectionItemDeletedMessage>,
    IRecipient<CollectionOpenedMessage>
{
    private readonly ITransportRegistry _transportRegistry;
    private readonly ICollectionService _collectionService;

    private EnvironmentModel? _activeEnvironment;
    private string _collectionPath = string.Empty;

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
    }

    [ObservableProperty]
    private IReadOnlyList<string> _availableFolders = [];

    public bool HasTabs => Tabs.Count > 0;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public RequestEditorViewModel(
        ITransportRegistry transportRegistry,
        ICollectionService collectionService,
        IMessenger messenger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(transportRegistry);
        ArgumentNullException.ThrowIfNull(collectionService);
        _transportRegistry = transportRegistry;
        _collectionService = collectionService;
        IsActive = true;

        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
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
        _collectionPath = message.Value;
        _ = UpdateAvailableFoldersAsync(message.Value);
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
            RemoveTab);

        tab.AvailableFolders = AvailableFolders;
        tab.CollectionRootPath = _collectionPath;
        tab.SaveAsFolderPath = string.Empty;

        if (request is not null)
            tab.LoadRequest(request);
        else
            tab.IsNew = true;

        tab.SetEnvironment(_activeEnvironment);
        return tab;
    }

    private async Task UpdateAvailableFoldersAsync(string collectionPath)
    {
        try
        {
            var root = await _collectionService.OpenFolderAsync(collectionPath);
            var folders = new List<string>();
            CollectFolderPaths(root, collectionPath, folders);
            AvailableFolders = folders.AsReadOnly();

            // Push updated list to any already-open new tabs.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var tab in Tabs.Where(t => t.IsNew))
                {
                    tab.AvailableFolders = AvailableFolders;
                    tab.CollectionRootPath = collectionPath;
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
}
