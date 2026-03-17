using System.Collections.ObjectModel;
using System.IO;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the full environment editor panel.
/// Allows the user to create, rename, and delete environments, and to manage the
/// variables within each environment (including secret variables).
///
/// The first item in the list is always the collection-scoped <em>Global</em> environment,
/// pinned, non-renamable, and non-deletable. Its variables are broadcast via
/// <see cref="GlobalEnvironmentChangedMessage"/> when loaded or saved.
///
/// Activated (and refreshed) whenever a <see cref="CollectionOpenedMessage"/> arrives.
/// Sends a <see cref="EnvironmentChangedMessage"/> after saving a regular environment so
/// the active environment in the request editor stays up-to-date.
/// </summary>
public sealed partial class EnvironmentEditorViewModel : ObservableRecipient,
    IRecipient<CollectionOpenedMessage>
{
    private readonly IEnvironmentService _environmentService;
    private readonly ICollectionPreferencesService _preferencesService;
    private readonly ILogger<EnvironmentEditorViewModel> _logger;

    private string? _collectionFolderPath;

    // ─── Observable state ────────────────────────────────────────────────────

    /// <summary>All environments found in the open collection.</summary>
    public ObservableCollection<EnvironmentListItemViewModel> Environments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    private EnvironmentListItemViewModel? _selectedEnvironment;
    /// <summary>True while a new-environment input row is being shown.</summary>
    [ObservableProperty]
    private bool _isAddingEnvironment;

    /// <summary>Draft name for the new environment.</summary>
    [ObservableProperty]
    private string _newEnvironmentName = string.Empty;

    /// <summary>Validation message for the new-environment name field.</summary>
    [ObservableProperty]
    private string _newEnvironmentError = string.Empty;

    /// <summary>True while any async operation is in progress.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Transient error message for display in the editor header.</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>True while the delete-confirmation overlay is shown.</summary>
    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>Name of the environment pending deletion — shown in the confirmation overlay.</summary>
    [ObservableProperty]
    private string _deleteConfirmationName = string.Empty;

    private EnvironmentListItemViewModel? _pendingDeleteItem;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public EnvironmentEditorViewModel(
        IEnvironmentService environmentService,
        ICollectionPreferencesService preferencesService,
        IMessenger messenger,
        ILogger<EnvironmentEditorViewModel> logger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(environmentService);
        ArgumentNullException.ThrowIfNull(preferencesService);
        ArgumentNullException.ThrowIfNull(logger);
        _environmentService = environmentService;
        _preferencesService = preferencesService;
        _logger = logger;
        IsActive = true;
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Shows the inline new-environment input row.</summary>
    [RelayCommand]
    private void BeginAddEnvironment()
    {
        NewEnvironmentName = string.Empty;
        NewEnvironmentError = string.Empty;
        IsAddingEnvironment = true;
    }

    /// <summary>Cancels the new-environment input row without creating anything.</summary>
    [RelayCommand]
    private void CancelAddEnvironment()
    {
        NewEnvironmentName = string.Empty;
        NewEnvironmentError = string.Empty;
        IsAddingEnvironment = false;
    }

    /// <summary>Creates the new environment on disk and adds it to the list.</summary>
    [RelayCommand]
    private async Task CommitAddEnvironmentAsync(CancellationToken ct)
    {
        if (_collectionFolderPath is null)
            return;

        var trimmed = NewEnvironmentName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            NewEnvironmentError = "Name cannot be empty.";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var model = await _environmentService
                .CreateEnvironmentAsync(_collectionFolderPath, trimmed, ct)
                .ConfigureAwait(true);

            var item = CreateListItem(model);
            Environments.Add(item);
            SelectedEnvironment = item;

            IsAddingEnvironment = false;
            NewEnvironmentName = string.Empty;
            NewEnvironmentError = string.Empty;

            // Persist the updated order (new item at end) and notify the selector
            // toolbar so it appears in the dropdown immediately, in the right position.
            await PersistCurrentOrderAsync().ConfigureAwait(true);
            if (_collectionFolderPath is not null)
                Messenger.Send(new EnvironmentOrderChangedMessage(_collectionFolderPath));
        }
        catch (InvalidOperationException ex)
        {
            NewEnvironmentError = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create environment '{Name}'", trimmed);
            ErrorMessage = "Failed to create environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows the inline confirmation overlay for the given item.</summary>
    private void BeginDelete(EnvironmentListItemViewModel item)
    {
        _pendingDeleteItem = item;
        DeleteConfirmationName = item.Name;
        IsConfirmingDelete = true;
    }

    /// <summary>Confirms deletion after the user acknowledges the confirmation overlay.</summary>
    [RelayCommand]
    private async Task ConfirmDeleteAsync(CancellationToken ct)
    {
        if (_pendingDeleteItem is null) return;
        var item = _pendingDeleteItem;
        _pendingDeleteItem = null;
        DeleteConfirmationName = string.Empty;
        IsConfirmingDelete = false;

        SelectedEnvironment = item;
        await DeleteEnvironmentAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Dismisses the confirmation overlay without deleting.</summary>
    [RelayCommand]
    private void CancelDelete()
    {
        _pendingDeleteItem = null;
        DeleteConfirmationName = string.Empty;
        IsConfirmingDelete = false;
    }

    /// <summary>Deletes the currently selected environment (never allowed for the global env).</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedDeletableEnvironment))]
    private async Task DeleteEnvironmentAsync(CancellationToken ct)
    {
        if (SelectedEnvironment is null || SelectedEnvironment.IsGlobal)
            return;

        var targetName = SelectedEnvironment.Name;
        var targetPath = SelectedEnvironment.FilePath;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _environmentService
                .DeleteEnvironmentAsync(targetPath, ct)
                .ConfigureAwait(true);

            var toRemove = Environments.FirstOrDefault(e => e.FilePath == targetPath);
            if (toRemove is not null)
                Environments.Remove(toRemove);

            SelectedEnvironment = Environments.Count > 1 ? Environments[1] : Environments[0];

            // Remove the deleted filename from prefs and notify the dropdown.
            await PersistCurrentOrderAsync().ConfigureAwait(true);
            if (_collectionFolderPath is not null)
                Messenger.Send(new EnvironmentOrderChangedMessage(_collectionFolderPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete environment '{Name}'", targetName);
            ErrorMessage = "Failed to delete environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Saves variable changes for the currently selected environment to disk.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedEnvironment))]
    private async Task SaveSelectedAsync(CancellationToken ct)
    {
        if (SelectedEnvironment is null)
            return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var model = SelectedEnvironment.BuildModel();

            if (SelectedEnvironment.IsGlobal)
            {
                // Global environment: persist via the dedicated global save path and broadcast.
                await _environmentService.SaveGlobalEnvironmentAsync(model, ct).ConfigureAwait(true);
                SelectedEnvironment.IsDirty = false;
                Messenger.Send(new GlobalEnvironmentChangedMessage(model.Variables));
            }
            else
            {
                await _environmentService.SaveEnvironmentAsync(model, ct).ConfigureAwait(true);
                SelectedEnvironment.IsDirty = false;
                // Notify the request editor so variable substitution uses the updated values.
                Messenger.Send(new EnvironmentSavedMessage(model));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save environment '{Name}'", SelectedEnvironment?.Name);
            ErrorMessage = "Failed to save environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Message handlers ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Receive(CollectionOpenedMessage message)
    {
        _collectionFolderPath = message.Value;
        _ = LoadEnvironmentsAsync(message.Value);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private bool HasSelectedEnvironment => SelectedEnvironment is not null;
    private bool HasSelectedDeletableEnvironment => SelectedEnvironment is { IsGlobal: false };

    /// <summary>
    /// Moves <paramref name="item"/> to <paramref name="targetIndex"/> in the
    /// <see cref="Environments"/> list, persists the new order to collection preferences,
    /// and notifies the environment dropdown to refresh its ordering.
    /// </summary>
    public async Task MoveEnvironmentAsync(EnvironmentListItemViewModel item, int targetIndex)
    {
        var currentIndex = Environments.IndexOf(item);
        if (currentIndex < 0 || currentIndex == targetIndex) return;
        // Index 0 is always the pinned Global env — never move to or from it.
        if (currentIndex == 0 || targetIndex <= 0 || targetIndex >= Environments.Count) return;

        Environments.Move(currentIndex, targetIndex);

        await PersistCurrentOrderAsync().ConfigureAwait(true);
        if (_collectionFolderPath is not null)
            Messenger.Send(new EnvironmentOrderChangedMessage(_collectionFolderPath));
    }

    /// <summary>Saves the current editor list order to collection preferences.</summary>
    private async Task PersistCurrentOrderAsync()
    {
        if (_collectionFolderPath is null) return;

        var orderedNames = Environments
            .Where(e => !e.IsGlobal)           // global env is always pinned — never in the order list
            .Select(e => Path.GetFileName(e.FilePath))
            .Where(n => !string.IsNullOrEmpty(n)) // exclude unsaved clone ghosts
            .ToList();

        try
        {
            var current = await _preferencesService
                .LoadAsync(_collectionFolderPath)
                .ConfigureAwait(false);
            await _preferencesService
                .SaveAsync(_collectionFolderPath, new()
                {
                    LastActiveEnvironmentFile = current.LastActiveEnvironmentFile,
                    OpenTabPaths = current.OpenTabPaths,
                    ActiveTabPath = current.ActiveTabPath,
                    EnvironmentOrder = orderedNames,
                })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist environment order");
        }
    }

    private async Task LoadEnvironmentsAsync(string collectionFolderPath)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var listTask = _environmentService.ListEnvironmentsAsync(collectionFolderPath);
            var globalTask = _environmentService.LoadGlobalEnvironmentAsync(collectionFolderPath);
            var prefsTask = _preferencesService.LoadAsync(collectionFolderPath);

            await Task.WhenAll(listTask, globalTask, prefsTask).ConfigureAwait(true);

            var list = listTask.Result;
            var globalModel = globalTask.Result;
            var prefs = prefsTask.Result;

            var orderedList = ApplyOrder(list, prefs.EnvironmentOrder);

            Environments.Clear();

            // Global env is always pinned first.
            Environments.Add(CreateListItem(globalModel, isGlobal: true));

            foreach (var model in orderedList)
                Environments.Add(CreateListItem(model));

            // Select the first non-global env, falling back to global if none present.
            SelectedEnvironment = Environments.Count > 1 ? Environments[1] : Environments[0];

            // Broadcast the current global vars so request tabs are up-to-date.
            Messenger.Send(new GlobalEnvironmentChangedMessage(globalModel.Variables));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load environments for '{Path}'", collectionFolderPath);
            ErrorMessage = "Failed to load environments.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reorders <paramref name="list"/> according to <paramref name="savedOrder"/>.
    /// Entries that no longer match an existing file are silently skipped.
    /// New environments not present in the saved order are appended at the end.
    /// </summary>
    private static IEnumerable<EnvironmentModel> ApplyOrder(
        IReadOnlyList<EnvironmentModel> list, IReadOnlyList<string>? savedOrder)
    {
        if (savedOrder is not { Count: > 0 })
            return list;

        var byFileName = list.ToDictionary(
            e => Path.GetFileName(e.FilePath),
            StringComparer.OrdinalIgnoreCase);

        var ordered = savedOrder
            .Where(byFileName.ContainsKey)
            .Select(name => byFileName[name]);

        var inOrder = new HashSet<string>(savedOrder, StringComparer.OrdinalIgnoreCase);
        var remaining = list.Where(e => !inOrder.Contains(Path.GetFileName(e.FilePath)));

        return ordered.Concat(remaining);
    }

    private EnvironmentListItemViewModel CreateListItem(EnvironmentModel model, bool isGlobal = false)
    {
        return new(
            model,
            onRenameCommit: RenameAsync,
            onDeleteRequest: (i, ct) => { BeginDelete(i); return Task.CompletedTask; },
            onCloneRequest: (i, ct) => CloneImmediateAsync(i, ct),
            isGlobal: isGlobal);
    }

    private async Task RenameAsync(
        EnvironmentListItemViewModel item, string newName, CancellationToken ct)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var renamedModel = await _environmentService
                .RenameEnvironmentAsync(item.FilePath, newName, ct)
                .ConfigureAwait(true);

            item.ApplyRename(renamedModel);

            // Prefs order stores filenames; after a rename the old filename becomes stale.
            // Persist the current order (now with the new filename) and notify the dropdown.
            await PersistCurrentOrderAsync().ConfigureAwait(true);
            if (_collectionFolderPath is not null)
                Messenger.Send(new EnvironmentOrderChangedMessage(_collectionFolderPath));
        }
        catch (InvalidOperationException ex)
        {
            item.RenameError = ex.Message;
            item.IsRenaming = true;   // reopen rename box to let the user correct the input
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename environment '{Name}' \u2192 '{NewName}'", item.Name, newName);
            ErrorMessage = "Failed to rename environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BeginClone(EnvironmentListItemViewModel sourceItem)
    {
        // Replaced by CloneImmediateAsync — kept as dead code guard.
    }

    private async Task CloneImmediateAsync(
        EnvironmentListItemViewModel sourceItem, CancellationToken ct)
    {
        if (_collectionFolderPath is null) return;

        // Build a unique "Copy of X" name, appending a counter if needed.
        var baseName = $"Copy of {sourceItem.Name}";
        var candidateName = baseName;
        var counter = 2;
        while (Environments.Any(e =>
            string.Equals(e.Name, candidateName, StringComparison.OrdinalIgnoreCase)))
        {
            candidateName = $"{baseName} ({counter++})";
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var cloned = await _environmentService
                .CloneEnvironmentAsync(sourceItem.FilePath, candidateName, ct)
                .ConfigureAwait(true);

            var newItem = CreateListItem(cloned);

            var sourceIndex = Environments.IndexOf(sourceItem);
            if (sourceIndex >= 0)
                Environments.Insert(sourceIndex + 1, newItem);
            else
                Environments.Add(newItem);

            SelectedEnvironment = newItem;

            await PersistCurrentOrderAsync().ConfigureAwait(true);
            if (_collectionFolderPath is not null)
                Messenger.Send(new EnvironmentOrderChangedMessage(_collectionFolderPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clone environment '{Name}'", sourceItem.Name);
            ErrorMessage = "Failed to clone environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
