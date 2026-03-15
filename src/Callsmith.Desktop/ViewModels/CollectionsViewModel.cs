using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the left-hand collections sidebar.
/// Owns the folder tree and handles all collection CRUD operations: open, create,
/// rename, delete, and navigation between requests.
/// </summary>
public sealed partial class CollectionsViewModel : ObservableRecipient,
    IRecipient<NavigationCancelledMessage>,
    IRecipient<CollectionRefreshRequestedMessage>,
    IRecipient<RequestSavedMessage>
{
    private readonly ICollectionService _collectionService;
    private readonly RecentCollectionsService _recentCollectionsService;
    private readonly ILogger<CollectionsViewModel> _logger;

    // -------------------------------------------------------------------------
    // Selection tracking (for navigation-guard revert)
    // -------------------------------------------------------------------------

    /// <summary>The item that was selected before the current (potentially pending) selection.</summary>
    private CollectionTreeItemViewModel? _previousSelectedItem;

    /// <summary>
    /// When true, the next <see cref="OnSelectedItemChanged"/> call will be suppressed
    /// (no message sent, no previous-item update). Used to revert the tree selection
    /// after a "stay here" navigation-cancel without triggering a load.
    /// </summary>
    private bool _suppressNextSelectionMessage;

    // -------------------------------------------------------------------------
    // File watcher
    // -------------------------------------------------------------------------

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watcherDebounce;

    /// <summary>
    /// Prevents the file watcher from triggering a refresh while we are purposely
    /// mutating the collection (create/rename/delete).
    /// </summary>
    private bool _suppressWatcher;

    // -------------------------------------------------------------------------
    // Observable properties
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<CollectionTreeItemViewModel> _treeRoots = [];

    [ObservableProperty]
    private CollectionTreeItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _collectionPath = string.Empty;

    [ObservableProperty]
    private bool _hasCollection;

    [ObservableProperty]
    private IReadOnlyList<string> _recentCollections = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentCollections))]
    private int _recentCollectionsCount;

    public bool HasRecentCollections => RecentCollections.Count > 0;

    /// <summary>Non-null while an inline delete confirmation is being shown for this node.</summary>
    [ObservableProperty]
    private CollectionTreeItemViewModel? _pendingDeleteNode;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public CollectionsViewModel(
        ICollectionService collectionService,
        RecentCollectionsService recentCollectionsService,
        IMessenger messenger,
        ILogger<CollectionsViewModel> logger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(recentCollectionsService);
        _collectionService = collectionService;
        _recentCollectionsService = recentCollectionsService;
        _logger = logger;
        IsActive = true;

        // Load recent collections in background (non-critical)
        _ = LoadRecentCollectionsAsync();
    }

    // -------------------------------------------------------------------------
    // Message receivers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called when RequestViewModel signals that the user cancelled navigation away
    /// from a dirty request. We revert the sidebar selection to the previously-open item.
    /// </summary>
    public void Receive(NavigationCancelledMessage message)
    {
        _suppressNextSelectionMessage = true;
        SelectedItem = _previousSelectedItem;
    }

    /// <summary>
    /// Called when a tab saves a new request to disk. Refreshes the tree so the new file appears.
    /// </summary>
    public void Receive(CollectionRefreshRequestedMessage message)
    {
        _ = RefreshAsync();
    }

    /// <summary>
    /// Called when a tab saves an existing request. Updates the matching tree node's
    /// in-memory snapshot so re-opening the tab shows the latest data.
    /// </summary>
    public void Receive(RequestSavedMessage message)
    {
        if (TreeRoots is not [var root]) return;
        var node = FindNodeByFilePath(root, message.Value.FilePath);
        node?.UpdateRequest(message.Value);
    }

    // -------------------------------------------------------------------------
    // Selection tracking
    // -------------------------------------------------------------------------

    partial void OnSelectedItemChanging(CollectionTreeItemViewModel? value)
    {
        // Capture the outgoing item unless we are in a programmatic suppress/revert.
        if (!_suppressNextSelectionMessage)
            _previousSelectedItem = SelectedItem;
    }

    partial void OnSelectedItemChanged(CollectionTreeItemViewModel? value)
    {
        if (_suppressNextSelectionMessage)
        {
            _suppressNextSelectionMessage = false;
            return;
        }

        if (value?.Request is CollectionRequest request)
            Messenger.Send(new RequestSelectedMessage(request));
    }

    // -------------------------------------------------------------------------
    // Open folder / refresh
    // -------------------------------------------------------------------------

    [RelayCommand]
    public async Task OpenFolderAsync(IStorageProvider storageProvider)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Collection Folder",
            AllowMultiple = false,
        });

        if (folders is not [var folder])
            return;

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        await LoadCollectionAsync(path);
    }

    [RelayCommand(CanExecute = nameof(HasCollection))]
    public async Task RefreshAsync()
    {
        if (!string.IsNullOrEmpty(CollectionPath))
            await LoadCollectionAsync(CollectionPath, retainSelection: true);
    }

    [RelayCommand]
    public async Task OpenRecentAsync(string path)
    {
        if (!string.IsNullOrEmpty(path))
            await LoadCollectionAsync(path);
    }

    // -------------------------------------------------------------------------
    // Inline rename
    // -------------------------------------------------------------------------

    [RelayCommand]
    public void BeginRename(CollectionTreeItemViewModel node)
    {
        if (node.IsRoot) return;
        node.EditingName = node.Name;
        node.IsRenaming = true;
    }

    [RelayCommand]
    public async Task CommitRenameAsync(CollectionTreeItemViewModel node)
    {
        var newName = node.EditingName.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            CancelRename(node);
            return;
        }

        // Creating a brand-new item (phantom node)
        if (node.IsNewNode)
        {
            await CommitNewNodeAsync(node, newName);
            return;
        }

        // Renaming an existing item
        _suppressWatcher = true;
        try
        {
            node.IsRenaming = false;

            if (!node.IsFolder)
            {
                var updated = await _collectionService.RenameRequestAsync(node.Request!.FilePath, newName);
                node.ApplyRename(newName, updated);
            }
            else
            {
                var updated = await _collectionService.RenameFolderAsync(node.FolderPath!, newName);
                node.ApplyFolderRename(newName, updated.FolderPath);
            }
        }
        catch (Exception ex)
        {
            // Revert name on failure
            _logger.LogWarning(ex, "Rename failed for node '{Name}'", node.Name);
            node.IsRenaming = false;
        }
        finally
        {
            _suppressWatcher = false;
        }
    }

    [RelayCommand]
    public void CancelRename(CollectionTreeItemViewModel node)
    {
        if (node.IsNewNode)
        {
            // Remove the phantom node from its parent
            node.Parent?.Children.Remove(node);
            return;
        }

        node.IsRenaming = false;
        node.EditingName = node.Name;
    }

    private async Task CommitNewNodeAsync(CollectionTreeItemViewModel phantom, string newName)
    {
        _suppressWatcher = true;
        try
        {
            phantom.IsRenaming = false;

            var parentFolderPath = phantom.Parent!.FolderPath!;

            if (!phantom.IsFolder)
            {
                var request = await _collectionService.CreateRequestAsync(parentFolderPath, newName);
                phantom.PromoteFromPhantom(newName, request: request);

                // Auto-select and open the newly-created request
                _suppressNextSelectionMessage = false;
                SelectedItem = phantom;
                Messenger.Send(new RequestSelectedMessage(request));
            }
            else
            {
                var folder = await _collectionService.CreateFolderAsync(parentFolderPath, newName);
                phantom.PromoteFromPhantom(newName, folderPath: folder.FolderPath);
            }
        }
        catch (Exception ex)
        {
            // Creation failed — remove the phantom
            _logger.LogWarning(ex, "Failed to create node '{Name}'", newName);
            phantom.Parent?.Children.Remove(phantom);
        }
        finally
        {
            _suppressWatcher = false;
        }
    }

    // -------------------------------------------------------------------------
    // Create new request / folder
    // -------------------------------------------------------------------------

    [RelayCommand]
    public void CreateRequest(CollectionTreeItemViewModel folder)
    {
        if (!folder.IsFolder) return;

        var baseName = "New Request";
        var finalName = PickUniqueRequestName(folder.FolderPath!, baseName);

        var phantom = CollectionTreeItemViewModel.NewPhantom(
            isFolder: false, defaultName: finalName, parent: folder);

        folder.IsExpanded = true;
        folder.Children.Add(phantom);
    }

    [RelayCommand]
    public void CreateFolder(CollectionTreeItemViewModel folder)
    {
        if (!folder.IsFolder) return;

        var baseName = "New Folder";
        var finalName = PickUniqueFolderName(folder.FolderPath!, baseName);

        var phantom = CollectionTreeItemViewModel.NewPhantom(
            isFolder: true, defaultName: finalName, parent: folder);

        folder.IsExpanded = true;
        folder.Children.Add(phantom);
    }

    // -------------------------------------------------------------------------
    // Delete
    // -------------------------------------------------------------------------

    [RelayCommand]
    public void DeleteNode(CollectionTreeItemViewModel node)
    {
        if (node.IsRoot) return;
        PendingDeleteNode = node;
    }

    [RelayCommand]
    public async Task ConfirmDeleteAsync()
    {
        if (PendingDeleteNode is not { } node) return;

        PendingDeleteNode = null;

        // Stop the watcher before deletion so it doesn't hold a directory
        // handle that prevents Directory.Delete from succeeding on Windows.
        StopWatcher();

        try
        {
            if (!node.IsFolder)
            {
                var deletedPath = node.Request!.FilePath;
                await _collectionService.DeleteRequestAsync(deletedPath);
                node.Parent?.Children.Remove(node);
                Messenger.Send(new CollectionItemDeletedMessage(deletedPath));
            }
            else
            {
                var deletedPath = node.FolderPath! + Path.DirectorySeparatorChar;
                await _collectionService.DeleteFolderAsync(node.FolderPath!);
                node.Parent?.Children.Remove(node);
                Messenger.Send(new CollectionItemDeletedMessage(deletedPath));
            }

            // Clear the sidebar selection if the deleted item was selected
            if (SelectedItem == node || IsAncestor(node, SelectedItem))
            {
                _suppressNextSelectionMessage = true;
                SelectedItem = null;
            }

            // Reload the tree to guarantee disk/UI consistency.
            // This also restarts the watcher via LoadCollectionAsync → StartWatcher.
            if (HasCollection)
                await LoadCollectionAsync(CollectionPath, retainSelection: true);
        }
        catch (Exception ex)
        {
            // Deletion failed — node stays in tree; restart watcher.
            _logger.LogWarning(ex, "Failed to delete node '{Name}'", node.Name);
            if (HasCollection)
                StartWatcher(CollectionPath);
        }
    }

    [RelayCommand]
    public void CancelDelete()
    {
        PendingDeleteNode = null;
    }

    // -------------------------------------------------------------------------
    // Drag reorder
    // -------------------------------------------------------------------------

    /// <summary>
    /// Moves a tree node to a new index within its parent's children, then persists the
    /// new order by writing the parent folder's <c>_order.json</c> file.
    /// </summary>
    public async Task MoveItemAsync(CollectionTreeItemViewModel item, int targetIndex)
    {
        if (item.IsRoot || item.IsNewNode || item.IsRenaming) return;

        var parent = item.Parent;
        if (parent?.FolderPath is null) return;

        var currentIndex = parent.Children.IndexOf(item);
        if (currentIndex < 0 || currentIndex == targetIndex) return;
        if (targetIndex < 0 || targetIndex >= parent.Children.Count) return;

        // Move in the observable collection immediately for instant visual feedback.
        parent.Children.Move(currentIndex, targetIndex);

        // Compute the ordered entry names from the updated children list.
        var orderedNames = parent.Children
            .Where(c => !c.IsNewNode)
            .Select(c => c.IsFolder ? c.Name : Path.GetFileName(c.Request!.FilePath))
            .ToList();

        _suppressWatcher = true;
        try
        {
            await _collectionService.SaveFolderOrderAsync(parent.FolderPath, orderedNames);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save folder order for '{Path}'", parent.FolderPath);
        }
        finally
        {
            _suppressWatcher = false;
        }
    }

    // -------------------------------------------------------------------------
    // Recently-opened collections
    // -------------------------------------------------------------------------

    private async Task LoadRecentCollectionsAsync()
    {
        RecentCollections = await _recentCollectionsService.LoadAsync();
        RecentCollectionsCount = RecentCollections.Count;

        if (RecentCollections.Count > 0)
            await LoadCollectionAsync(RecentCollections[0]);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task LoadCollectionAsync(string path, bool retainSelection = false)
    {
        // Remember which request was open so we can restore selection after rebuild.
        var openFilePath = retainSelection ? SelectedItem?.Request?.FilePath : null;
        var expandedPaths = retainSelection ? CollectExpandedFolderPaths(TreeRoots) : null;

        var root = await _collectionService.OpenFolderAsync(path);
        CollectionPath = path;
        HasCollection = true;

        var rootNode = CollectionTreeItemViewModel.FromFolder(root, parent: null, isRoot: true);

        if (expandedPaths is not null)
            RestoreExpandedState(rootNode, expandedPaths);

        TreeRoots = [rootNode];
        RefreshCommand.NotifyCanExecuteChanged();
        Messenger.Send(new CollectionOpenedMessage(path));

        // Restore the previously-open request selection silently (no reload).
        if (openFilePath is not null)
        {
            var found = FindNodeByFilePath(rootNode, openFilePath);
            if (found is not null)
            {
                _suppressNextSelectionMessage = true;
                SelectedItem = found;
            }
        }

        // Track recently opened (non-blocking)
        _ = _recentCollectionsService.PushAsync(path).ContinueWith(async _ =>
        {
            var updated = await _recentCollectionsService.LoadAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RecentCollections = updated;
                RecentCollectionsCount = updated.Count;
            });
        }, TaskScheduler.Default);

        StartWatcher(path);
    }

    private static string PickUniqueRequestName(string folderPath, string baseName)
    {
        var name = baseName;
        var counter = 1;
        while (File.Exists(Path.Combine(folderPath, name + Core.Services.FileSystemCollectionService.RequestFileExtension)))
            name = $"{baseName} {++counter}";
        return name;
    }

    private static string PickUniqueFolderName(string parentPath, string baseName)
    {
        var name = baseName;
        var counter = 1;
        while (Directory.Exists(Path.Combine(parentPath, name)))
            name = $"{baseName} {++counter}";
        return name;
    }

    private static bool IsAncestor(
        CollectionTreeItemViewModel potentialAncestor,
        CollectionTreeItemViewModel? node)
    {
        var current = node?.Parent;
        while (current is not null)
        {
            if (current == potentialAncestor) return true;
            current = current.Parent;
        }
        return false;
    }

    private static CollectionTreeItemViewModel? FindNodeByFilePath(
        CollectionTreeItemViewModel node, string filePath)
    {
        if (!node.IsFolder && node.Request is not null &&
            string.Equals(node.Request.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            return node;

        foreach (var child in node.Children)
        {
            var found = FindNodeByFilePath(child, filePath);
            if (found is not null) return found;
        }

        return null;
    }

    private static HashSet<string> CollectExpandedFolderPaths(
        IEnumerable<CollectionTreeItemViewModel> roots)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            CollectExpanded(root, set);
        return set;
    }

    private static void CollectExpanded(CollectionTreeItemViewModel node, HashSet<string> set)
    {
        if (node.IsFolder && node.IsExpanded && node.FolderPath is not null)
            set.Add(node.FolderPath);
        foreach (var child in node.Children)
            CollectExpanded(child, set);
    }

    private static void RestoreExpandedState(
        CollectionTreeItemViewModel node, HashSet<string> expandedPaths)
    {
        if (node.IsFolder && node.FolderPath is not null)
            node.IsExpanded = expandedPaths.Contains(node.FolderPath);
        foreach (var child in node.Children)
            RestoreExpandedState(child, expandedPaths);
    }

    // -------------------------------------------------------------------------
    // File system watcher
    // -------------------------------------------------------------------------

    private void StartWatcher(string path)
    {
        StopWatcher();

        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnWatcherEvent;
            _watcher.Deleted += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
        }
        catch (Exception ex)
        {
            // Watching is best-effort; don't crash if the OS rejects it
            _logger.LogWarning(ex, "Failed to start FileSystemWatcher for '{Path}'", path);
            _watcher = null;
        }
    }

    private void StopWatcher()
    {
        _watcherDebounce?.Cancel();
        _watcherDebounce = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        if (_suppressWatcher) return;

        _watcherDebounce?.Cancel();
        _watcherDebounce = new CancellationTokenSource();
        var ct = _watcherDebounce.Token;

        Task.Delay(600, ct).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(async () =>
            {
                if (!ct.IsCancellationRequested)
                    await LoadCollectionAsync(CollectionPath, retainSelection: true);
            });
        }, TaskScheduler.Default);
    }
}

