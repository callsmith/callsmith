using System.Text.RegularExpressions;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Bruno;
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
    IRecipient<CollectionRefreshRequestedMessage>,
    IRecipient<RequestSavedMessage>,
    IRecipient<ActiveTabChangedMessage>,
    IRecipient<RevealRequestMessage>
{
    private readonly ICollectionService _collectionService;
    private readonly IRecentCollectionsService _recentCollectionsService;
    private readonly ICollectionImportService _importService;
    private readonly ICollectionPreferencesService _preferencesService;
    private readonly IHistoryService _historyService;
    private readonly ILogger<CollectionsViewModel> _logger;

    // Cancels any in-flight LoadCollectionAsync when a newer one starts.
    private CancellationTokenSource? _loadCts;

    // -------------------------------------------------------------------------
    // Active request tracking
    // -------------------------------------------------------------------------

    /// <summary>File path of the request currently open in the active tab. Empty when none.</summary>
    private string _activeFilePath = string.Empty;

    // -------------------------------------------------------------------------
    // Rename / create-folder dialog state
    // -------------------------------------------------------------------------

    /// <summary>Non-null while the rename dialog is open for an existing node.</summary>
    private CollectionTreeItemViewModel? _renameTargetNode;

    /// <summary>Non-null while the create-folder dialog is open; holds the parent folder.</summary>
    private CollectionTreeItemViewModel? _renameParentFolder;

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
    private string _collectionPath = string.Empty;

    [ObservableProperty]
    private bool _hasCollection;

    [ObservableProperty]
    private bool _isBrunoCollection;

    [ObservableProperty]
    private IReadOnlyList<string> _recentCollections = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentCollections))]
    private int _recentCollectionsCount;

    public bool HasRecentCollections => RecentCollections.Count > 0;

    [ObservableProperty]
    private bool _isRecentPanelOpen;

    /// <summary>Non-null while an inline delete confirmation is being shown for this node.</summary>
    [ObservableProperty]
    private CollectionTreeItemViewModel? _pendingDeleteNode;

    [ObservableProperty]
    private bool _isRenameDialogOpen;

    [ObservableProperty]
    private string _renameDialogTitle = string.Empty;

    [ObservableProperty]
    private string _renameDialogValue = string.Empty;

    [ObservableProperty]
    private string _renameDialogError = string.Empty;

    [ObservableProperty]
    private string _renameDialogConfirmLabel = string.Empty;

    /// <summary>
    /// Set to the file path of a request that should be revealed (ancestors expanded,
    /// node highlighted) in the tree. The view observes this and performs the visual
    /// tree expansion. Reset to empty string after the view has processed it.
    /// </summary>
    [ObservableProperty]
    private string _revealFilePath = string.Empty;

    /// <summary>
    /// Non-null while the import-collection dialog is open.
    /// The view observes this property and opens the dialog window.
    /// </summary>
    [ObservableProperty]
    private ImportCollectionViewModel? _pendingImportDialog;

    /// <summary>
    /// Non-null while the folder settings dialog is open.
    /// The view observes this property and opens the dialog window.
    /// </summary>
    [ObservableProperty]
    private FolderSettingsViewModel? _pendingFolderSettings;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public CollectionsViewModel(
        ICollectionService collectionService,
        IRecentCollectionsService recentCollectionsService,
        ICollectionImportService importService,
        ICollectionPreferencesService preferencesService,
        IHistoryService historyService,
        IMessenger messenger,
        ILogger<CollectionsViewModel> logger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(recentCollectionsService);
        ArgumentNullException.ThrowIfNull(importService);
        ArgumentNullException.ThrowIfNull(preferencesService);
        ArgumentNullException.ThrowIfNull(historyService);
        _collectionService = collectionService;
        _recentCollectionsService = recentCollectionsService;
        _importService = importService;
        _preferencesService = preferencesService;
        _historyService = historyService;
        _logger = logger;
        IsActive = true;

        // Load recent collections in background (non-critical)
        _ = LoadRecentCollectionsAsync();
    }

    // -------------------------------------------------------------------------
    // Message receivers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called when the active tab changes. Updates the sidebar active-node indicator
    /// and signals the view to expand ancestors so the active request is visible.
    /// </summary>
    public void Receive(ActiveTabChangedMessage message)
    {
        _activeFilePath = message.Value;
        ApplyActiveState();
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

    /// <summary>
    /// Called after the command palette opens a request, to expand the collection sidebar
    /// and highlight that request node.
    /// </summary>
    public void Receive(RevealRequestMessage message)
    {
        _activeFilePath = message.Value;
        ApplyActiveState();
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

    /// <summary>
    /// Opens the import-collection modal dialog.
    /// The view observes <see cref="PendingImportDialog"/> and shows the window.
    /// Call <see cref="OnImportDialogClosedAsync"/> when the dialog window closes.
    /// </summary>
    [RelayCommand]
    public void OpenImportDialog()
    {
        PendingImportDialog = new ImportCollectionViewModel(_importService);
    }

    /// <summary>
    /// Called by the view after the import dialog window is closed.
    /// If the user confirmed a successful import, loads the imported collection.
    /// </summary>
    public async Task OnImportDialogClosedAsync()
    {
        var dialog = PendingImportDialog;
        PendingImportDialog = null;

        if (dialog is { IsConfirmed: true, ResultFolderPath: var folder }
            && !string.IsNullOrEmpty(folder))
        {
            await LoadCollectionAsync(folder);
        }
    }

    /// <summary>
    /// Opens the folder settings modal dialog for the given folder node.
    /// The view observes <see cref="PendingFolderSettings"/> and shows the window.
    /// </summary>
    [RelayCommand]
    public void OpenFolderSettings(CollectionTreeItemViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        PendingFolderSettings = new FolderSettingsViewModel(node, _collectionService);
    }

    /// <summary>
    /// Called by the view after the folder settings dialog window is closed.
    /// Updates the node's in-memory auth snapshot with the saved settings.
    /// </summary>
    public void OnFolderSettingsDialogClosed(CollectionTreeItemViewModel node)
    {
        var dialog = PendingFolderSettings;
        PendingFolderSettings = null;

        if (dialog is null) return;

        // Update the in-memory node so the tree reflects the new auth without a full reload.
        var auth = new AuthConfig
        {
            AuthType = dialog.AuthType,
            Token = string.IsNullOrEmpty(dialog.AuthToken) ? null : dialog.AuthToken,
            Username = string.IsNullOrEmpty(dialog.AuthUsername) ? null : dialog.AuthUsername,
            Password = string.IsNullOrEmpty(dialog.AuthPassword) ? null : dialog.AuthPassword,
            ApiKeyName = string.IsNullOrEmpty(dialog.AuthApiKeyName) ? null : dialog.AuthApiKeyName,
            ApiKeyValue = string.IsNullOrEmpty(dialog.AuthApiKeyValue) ? null : dialog.AuthApiKeyValue,
            ApiKeyIn = dialog.AuthApiKeyIn,
        };
        node.UpdateFolderAuth(auth);
    }

    [RelayCommand(CanExecute = nameof(HasCollection))]
    public async Task RefreshAsync()
    {
        if (!string.IsNullOrEmpty(CollectionPath))
            await LoadCollectionAsync(CollectionPath, retainExpansion: true);
    }

    [RelayCommand]
    public async Task OpenRecentAsync(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            IsRecentPanelOpen = false;
            await LoadCollectionAsync(path);
        }
    }

    [RelayCommand]
    private void ToggleRecentPanel() => IsRecentPanelOpen = !IsRecentPanelOpen;

    /// <summary>Opens a request node â€” called directly from the sidebar on every click.</summary>
    [RelayCommand]
    public async Task OpenRequestAsync(CollectionTreeItemViewModel node)
    {
        if (node.Request is not CollectionRequest treeRequest) return;
        try
        {
            // Always re-load from disk so that secrets (e.g. Basic auth passwords) are
            // fetched from local secret storage rather than reading the stale in-memory
            // tree node, which was populated by the synchronous folder-scan and never
            // retrieves secrets.
            var request = await _collectionService.LoadRequestAsync(treeRequest.FilePath);
            Messenger.Send(new RequestSelectedMessage(request));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load request '{Path}'", treeRequest.FilePath);
        }
    }

    [RelayCommand]
    public void ViewRequestHistory(CollectionTreeItemViewModel node)
    {
        if (node.Request?.RequestId is not { } requestId)
            return;

        Messenger.Send(new OpenHistoryMessage(requestId, node.Request.Name));
    }

    // -------------------------------------------------------------------------
    // Rename dialog (modal)
    // -------------------------------------------------------------------------

    /// <summary>Opens the rename/create dialog for an existing node.</summary>
    [RelayCommand]
    public void BeginRename(CollectionTreeItemViewModel node)
    {
        if (node.IsRoot) return;
        _renameTargetNode = node;
        _renameParentFolder = null;
        RenameDialogTitle = node.IsFolder ? "Rename Folder" : "Rename Request";
        RenameDialogValue = node.Name;
        RenameDialogError = string.Empty;
        RenameDialogConfirmLabel = "Rename";
        IsRenameDialogOpen = true;
    }

    [RelayCommand]
    public async Task CommitRenameDialogAsync()
    {
        var newName = RenameDialogValue.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            RenameDialogError = "Name cannot be empty.";
            return;
        }

        IsRenameDialogOpen = false;
        RenameDialogError = string.Empty;

        if (_renameTargetNode is { } node)
        {
            _suppressWatcher = true;
            try
            {
                if (!node.IsFolder)
                {
                    var oldFilePath = node.Request!.FilePath;
                    var updated = await _collectionService.RenameRequestAsync(oldFilePath, newName);
                    node.ApplyRename(newName, updated);
                    
                    // Notify open tabs of the rename so they can update their state,
                    // session persistence, and dynamic cache keys.
                    Messenger.Send(new RequestRenamedMessage(oldFilePath, updated));
                }
                else
                {
                    var oldFolderPath = node.FolderPath!;
                    var updated = await _collectionService.RenameFolderAsync(oldFolderPath, newName);
                    node.ApplyFolderRename(newName, updated.FolderPath);
                    
                    // Persist the updated expanded state so the new folder path is saved
                    // (old path will no longer be expanded in preferences).
                    await PersistExpandedStateAsync().ConfigureAwait(true);
                    
                    // When a folder is renamed, all requests under it are affected.
                    // Collect all affected requests and send rename messages for each.
                    var affectedRequests = node.GetAllRequestsUnder().ToList();
                    foreach (var requestNode in affectedRequests)
                    {
                        if (requestNode.Request is null) continue;
                        
                        // Compute old file path (before rename).
                        var oldRequestFilePath = requestNode.Request.FilePath;
                        
                        // Compute new file path: replace the old folder path prefix with the new one.
                        var newRequestFilePath = oldRequestFilePath.Replace(oldFolderPath, updated.FolderPath, StringComparison.OrdinalIgnoreCase);
                        
                        // Create updated request with new file path.
                        var updatedRequest = new CollectionRequest
                        {
                            RequestId = requestNode.Request.RequestId,
                            FilePath = newRequestFilePath,
                            Name = requestNode.Request.Name,
                            Method = requestNode.Request.Method,
                            Url = requestNode.Request.Url,
                            Description = requestNode.Request.Description,
                            Headers = requestNode.Request.Headers,
                            BodyType = requestNode.Request.BodyType,
                            Body = requestNode.Request.Body,
                            QueryParams = requestNode.Request.QueryParams,
                            PathParams = requestNode.Request.PathParams,
                            Auth = requestNode.Request.Auth,
                            FormParams = requestNode.Request.FormParams,
                        };
                        requestNode.UpdateRequest(updatedRequest);
                        
                        // Notify open tabs and environment variables of the rename.
                        Messenger.Send(new RequestRenamedMessage(oldRequestFilePath, updatedRequest));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rename failed for node '{Name}'", node.Name);
            }
            finally
            {
                _suppressWatcher = false;
            }
        }
        else if (_renameParentFolder is { } parentFolder)
        {
            _suppressWatcher = true;
            try
            {
                var folder = await _collectionService.CreateFolderAsync(parentFolder.FolderPath!, newName);
                var newNode = CollectionTreeItemViewModel.FromFolder(folder, parent: parentFolder);
                parentFolder.IsExpanded = true;
                parentFolder.Children.Add(newNode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create folder '{Name}'", newName);
            }
            finally
            {
                _suppressWatcher = false;
            }
        }
    }

    [RelayCommand]
    public void CancelRenameDialog()
    {
        IsRenameDialogOpen = false;
        RenameDialogError = string.Empty;
    }

    // -------------------------------------------------------------------------
    // Create new request / folder
    // -------------------------------------------------------------------------

    /// <summary>Directly creates a request with a default name and opens it immediately.</summary>
    [RelayCommand]
    public async Task CreateRequestAsync(CollectionTreeItemViewModel folder)
    {
        if (!folder.IsFolder) return;

        var finalName = PickUniqueRequestName(folder.FolderPath!, "New Request");

        _suppressWatcher = true;
        try
        {
            var request = await _collectionService.CreateRequestAsync(folder.FolderPath!, finalName);
            var newNode = CollectionTreeItemViewModel.FromRequest(request, parent: folder);
            folder.IsExpanded = true;
            folder.Children.Add(newNode);

            // Open the newly-created request immediately
            Messenger.Send(new RequestSelectedMessage(request));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create request '{Name}'", finalName);
        }
        finally
        {
            _suppressWatcher = false;
        }
    }

    /// <summary>Opens the create-folder dialog with a unique default name.</summary>
    [RelayCommand]
    public void CreateFolder(CollectionTreeItemViewModel folder)
    {
        if (!folder.IsFolder) return;

        _renameTargetNode = null;
        _renameParentFolder = folder;
        RenameDialogTitle = "New Folder";
        RenameDialogValue = PickUniqueFolderName(folder.FolderPath!, "New Folder");
        RenameDialogError = string.Empty;
        RenameDialogConfirmLabel = "Create";
        IsRenameDialogOpen = true;
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

            // Reload the tree to guarantee disk/UI consistency.
            // This also restarts the watcher via LoadCollectionAsync â†’ StartWatcher.
            if (HasCollection)
                await LoadCollectionAsync(CollectionPath, retainExpansion: true);
        }
        catch (Exception ex)
        {
            // Deletion failed â€” node stays in tree; restart watcher.
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
    /// new order by writing the parent folder's <c>_meta.json</c> file.
    /// </summary>
    public async Task MoveItemAsync(CollectionTreeItemViewModel item, int targetIndex)
    {
        if (item.IsRoot) return;

        var parent = item.Parent;
        if (parent?.FolderPath is null) return;

        var currentIndex = parent.Children.IndexOf(item);
        if (currentIndex < 0 || currentIndex == targetIndex) return;
        if (targetIndex < 0 || targetIndex >= parent.Children.Count) return;

        // Move in the observable collection immediately for instant visual feedback.
        parent.Children.Move(currentIndex, targetIndex);

        // Compute the ordered entry names from the updated children list.
        var orderedNames = parent.Children
            .Select(c => c.IsFolder ? c.Name : Path.GetFileName(c.Request!.FilePath.Replace('\\', '/')))
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

    /// <summary>
    /// Moves a request to another folder by updating the underlying file and
    /// refreshing the tree from disk, while preserving expanded folders.
    /// </summary>
    /// <param name="insertAtIndex">
    /// Zero-based index at which to insert the request in the destination folder's child order.
    /// Pass -1 to append at the end (e.g., when dropping directly onto a folder icon).
    /// </param>
    public async Task MoveRequestToFolderAsync(
        CollectionTreeItemViewModel requestNode,
        CollectionTreeItemViewModel destinationFolder,
        int insertAtIndex = -1)
    {
        if (requestNode is null || destinationFolder is null) return;
        if (requestNode.IsRoot || requestNode.IsFolder) return;
        if (requestNode.Request is null) return;
        if (string.IsNullOrEmpty(destinationFolder.FolderPath)) return;

        var sourceFolderPath = requestNode.Parent?.FolderPath;
        if (string.IsNullOrEmpty(sourceFolderPath)) return;

        if (string.Equals(sourceFolderPath, destinationFolder.FolderPath, StringComparison.OrdinalIgnoreCase))
            return;

        // Build the ordered name list for the destination folder, inserting the
        // moved request at the exact slot the drop indicator showed.
        var requestFileName = Path.GetFileName(requestNode.Request.FilePath.Replace('\\', '/'));
        var destNames = destinationFolder.Children
            .Select(c => c.IsFolder ? c.Name : Path.GetFileName(c.Request!.FilePath.Replace('\\', '/')))
            .ToList();
        if (insertAtIndex >= 0 && insertAtIndex <= destNames.Count)
            destNames.Insert(insertAtIndex, requestFileName);
        else
            destNames.Add(requestFileName);

        _suppressWatcher = true;
        try
        {
            await _collectionService.MoveRequestAsync(requestNode.Request.FilePath, destinationFolder.FolderPath);
            await _collectionService.SaveFolderOrderAsync(destinationFolder.FolderPath, destNames);

            if (!string.IsNullOrEmpty(CollectionPath))
                await LoadCollectionAsync(CollectionPath, retainExpansion: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to move request '{Path}' to '{Destination}'", requestNode.Request.FilePath, destinationFolder.FolderPath);
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
        try
        {
            RecentCollections = await _recentCollectionsService.LoadAsync();
            RecentCollectionsCount = RecentCollections.Count;

            if (RecentCollections.Count > 0)
                await LoadCollectionAsync(RecentCollections[0]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent collections");
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task LoadCollectionAsync(string path, bool retainExpansion = false)
    {
        // Cancel any in-flight load so stale results cannot overwrite UI state.
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        // For a tree refresh: capture current in-memory expansion state.
        // For a fresh open: restore from persisted preferences.
        HashSet<string>? expandedPaths;
        if (retainExpansion)
        {
            expandedPaths = CollectExpandedFolderPaths(TreeRoots);
        }
        else
        {
            CollectionPreferences prefs;
            try { prefs = await _preferencesService.LoadAsync(path, cts.Token); }
            catch (OperationCanceledException) { return; }

            // Null means "never saved" â€” default to all collapsed.
            // An empty list also means all collapsed (user explicitly closed everything).
            expandedPaths = (prefs.ExpandedFolderPaths ?? [])
                .Select(r => Path.GetFullPath(Path.Combine(path, r)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        CollectionFolder root;
        try { root = await _collectionService.OpenFolderAsync(path, cts.Token); }
        catch (OperationCanceledException) { return; }

        // A newer load has already started â€” silently abandon this one.
        if (cts.IsCancellationRequested) return;

        CollectionPath = path;
        HasCollection = true;
        IsBrunoCollection = BrunoDetector.IsBrunoCollection(path);

        // Switch the history database to the one associated with this collection.
        await _historyService.SetCollectionAsync(path, cts.Token);

        var rootNode = CollectionTreeItemViewModel.FromFolder(root, parent: null, isRoot: true);

        if (expandedPaths is not null)
            RestoreExpandedState(rootNode, expandedPaths);

        TreeRoots = [rootNode];
        RefreshCommand.NotifyCanExecuteChanged();

        // Only notify other ViewModels that a collection was opened when this is a
        // genuine new-collection load (not a tree refresh after delete/rename/watcher).
        // Refresh calls use retainExpansion=true; opening a new collection uses false.
        // Sending CollectionOpenedMessage on a refresh incorrectly resets the active
        // environment selection and clears/restores all open tabs.
        if (!retainExpansion)
            Messenger.Send(new CollectionOpenedMessage(path));

        // Re-apply active indicator after tree rebuild.
        ApplyActiveState();

        // Track recently opened (non-blocking)
        _ = UpdateRecentCollectionsAfterPushAsync(path);

        StartWatcher(path);

        // Subscribe to IsExpanded changes so future expand/collapse is persisted.
        SubscribeToExpansionChanges(rootNode);

        // After a tree rebuild (delete/rename/watcher refresh) the in-memory expansion
        // state is already correct but the prefs file may still reference stale paths
        // (e.g. a just-deleted folder). Flush the clean state now so the file is always
        // accurate, even if the user closes the app before touching the tree again.
        if (retainExpansion)
            _ = PersistExpandedStateAsync();
    }

    private void ApplyActiveState()
    {
        if (TreeRoots is not [var root]) return;
        SetActiveNode(root, _activeFilePath);
    }

    private static void SetActiveNode(CollectionTreeItemViewModel node, string activeFilePath)
    {
        if (!node.IsFolder)
        {
            node.IsActive = !string.IsNullOrEmpty(activeFilePath) &&
                            string.Equals(node.Request?.FilePath, activeFilePath,
                                          StringComparison.OrdinalIgnoreCase);
        }
        foreach (var child in node.Children)
            SetActiveNode(child, activeFilePath);
    }

    private async Task UpdateRecentCollectionsAfterPushAsync(string path)
    {
        try
        {
            await _recentCollectionsService.PushAsync(path).ConfigureAwait(false);
            var updated = await _recentCollectionsService.LoadAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RecentCollections = updated;
                RecentCollectionsCount = updated.Count;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update recent collections");
        }
    }

    private string PickUniqueRequestName(string folderPath, string baseName)
    {
        var ext = _collectionService.RequestFileExtension;
        var name = baseName;
        var counter = 1;
        while (File.Exists(Path.Combine(folderPath, name + ext)))
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

    /// <summary>
    /// Searches the in-memory collection tree for a request by its stable <see cref="CollectionRequest.RequestId"/>.
    /// Returns <see langword="null"/> when no collection is open or the request is not found.
    /// </summary>
    public CollectionRequest? FindRequestByRequestId(Guid requestId)
    {
        if (TreeRoots is not [var root]) return null;
        return FindRequestByRequestId(root, requestId);
    }

    private static CollectionRequest? FindRequestByRequestId(CollectionTreeItemViewModel node, Guid requestId)
    {
        if (!node.IsFolder && node.Request?.RequestId == requestId)
            return node.Request;

        foreach (var child in node.Children)
        {
            var found = FindRequestByRequestId(child, requestId);
            if (found is not null) return found;
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Expansion state persistence
    // -------------------------------------------------------------------------

    // Cleanup actions for event handlers wired to the current tree's nodes.
    // Cleared and re-populated each time the tree is rebuilt.
    private readonly List<Action> _expansionCleanups = [];

    /// <summary>
    /// Walks <paramref name="root"/> and subscribes to <c>IsExpanded</c> changes on
    /// every folder node (and to <c>Children.CollectionChanged</c> so newly-added
    /// sub-folders are also covered). Old subscriptions are unregistered first.
    /// </summary>
    private void SubscribeToExpansionChanges(CollectionTreeItemViewModel root)
    {
        foreach (var cleanup in _expansionCleanups)
            cleanup();
        _expansionCleanups.Clear();
        SubscribeRecursive(root);
    }

    private void SubscribeRecursive(CollectionTreeItemViewModel node)
    {
        if (!node.IsFolder) return;

        void PropHandler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CollectionTreeItemViewModel.IsExpanded))
                _ = PersistExpandedStateAsync();
        }
        node.PropertyChanged += PropHandler;
        _expansionCleanups.Add(() => node.PropertyChanged -= PropHandler);

        void ChildHandler(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is null) return;
            foreach (CollectionTreeItemViewModel newChild in e.NewItems)
                SubscribeRecursive(newChild);
        }
        node.Children.CollectionChanged += ChildHandler;
        _expansionCleanups.Add(() => node.Children.CollectionChanged -= ChildHandler);

        foreach (var child in node.Children)
            SubscribeRecursive(child);
    }

    private async Task PersistExpandedStateAsync()
    {
        if (string.IsNullOrEmpty(CollectionPath)) return;
        try
        {
            var collectionPath = CollectionPath;
            var expandedRelative = CollectExpandedFolderPaths(TreeRoots)
                .Select(abs => Path.GetRelativePath(collectionPath, abs))
                .ToList();

            await _preferencesService.UpdateAsync(collectionPath, current => new()
            {
                LastActiveEnvironmentFile = current.LastActiveEnvironmentFile,
                OpenTabPaths = current.OpenTabPaths,
                ActiveTabPath = current.ActiveTabPath,
                ExpandedFolderPaths = expandedRelative.AsReadOnly(),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist expanded state for '{Path}'", CollectionPath);
        }
    }

    /// <summary>
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

        // Only react to directory events (no extension) or recognised request files:
        //   â€¢ Callsmith: *.callsmith  (but NOT *.env.callsmith â€” env files don't affect the tree)
        //   â€¢ Bruno:     *.bru        (but NOT ones inside an "environments" folder)
        var ext = Path.GetExtension(e.FullPath);
        var isDirectory = string.IsNullOrEmpty(ext);
        var isCallsmithRequest = !isDirectory
            && e.FullPath.EndsWith(FileSystemCollectionService.RequestFileExtension, StringComparison.OrdinalIgnoreCase)
            && !e.FullPath.EndsWith(FileSystemEnvironmentService.EnvironmentFileExtension, StringComparison.OrdinalIgnoreCase);
        var isBrunoRequest = !isDirectory
            && e.FullPath.EndsWith(BrunoCollectionService.RequestFileExtension, StringComparison.OrdinalIgnoreCase)
            && !IsUnderBrunoEnvironmentsFolder(e.FullPath);

        if (!isDirectory && !isCallsmithRequest && !isBrunoRequest)
            return;

        // Marshal entirely to the UI thread so no locking is needed for the
        // CancellationTokenSource swap.
        Dispatcher.UIThread.Post(() =>
        {
            _watcherDebounce?.Cancel();
            _watcherDebounce?.Dispose();
            _watcherDebounce = new CancellationTokenSource();
            var ct = _watcherDebounce.Token;

            Task.Delay(600, ct).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.UIThread.Post(async () =>
                {
                    if (!ct.IsCancellationRequested)
                        await LoadCollectionAsync(CollectionPath, retainExpansion: true);
                });
            }, TaskScheduler.Default);
        });
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="filePath"/> lives inside a Bruno
    /// <c>environments/</c> folder so those files are excluded from tree-refresh triggers.
    /// </summary>
    private static bool IsUnderBrunoEnvironmentsFolder(string filePath) =>
        filePath.Contains(
            Path.DirectorySeparatorChar + BrunoCollectionService.EnvironmentFolderName + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
        || filePath.Contains(
            Path.AltDirectorySeparatorChar + BrunoCollectionService.EnvironmentFolderName + Path.AltDirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

