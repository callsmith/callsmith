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
    private readonly ICollectionService _collectionService;
    private readonly IDynamicVariableEvaluator _dynamicEvaluator;
    private readonly ILogger<EnvironmentEditorViewModel> _logger;

    private string? _collectionFolderPath;
    private List<string> _availableRequestNames = [];
    private TaskCompletionSource<DynamicValueSegment?>? _pendingDynamicConfigTcs;
    private TaskCompletionSource<MockDataSegment?>? _pendingMockDataConfigTcs;

    // ─── Observable state ────────────────────────────────────────────────────

    /// <summary>All environments found in the open collection.</summary>
    public ObservableCollection<EnvironmentListItemViewModel> Environments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
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

    /// <summary>
    /// Set to the dynamic value config ViewModel just before <see cref="ShowDynamicValueConfig"/>
    /// becomes true. The view uses this to populate and show the configuration dialog.
    /// </summary>
    public DynamicValueConfigViewModel? PendingDynamicConfig { get; private set; }

    /// <summary>
    /// Setting this to true signals the view to open the dynamic value config dialog.
    /// The view resets it to false after the dialog closes.
    /// </summary>
    [ObservableProperty]
    private bool _showDynamicValueConfig;

    /// <summary>
    /// Set to the mock data config ViewModel just before <see cref="ShowMockDataConfig"/>
    /// becomes true.
    /// </summary>
    public MockDataConfigViewModel? PendingMockDataConfig { get; private set; }

    /// <summary>
    /// Setting this to true signals the view to open the mock data picker dialog.
    /// The view resets it to false after the dialog closes.
    /// </summary>
    [ObservableProperty]
    private bool _showMockDataConfig;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public EnvironmentEditorViewModel(
        IEnvironmentService environmentService,
        ICollectionService collectionService,
        IDynamicVariableEvaluator dynamicEvaluator,
        IMessenger messenger,
        ILogger<EnvironmentEditorViewModel> logger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(environmentService);
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(dynamicEvaluator);
        ArgumentNullException.ThrowIfNull(logger);
        _environmentService = environmentService;
        _collectionService = collectionService;
        _dynamicEvaluator = dynamicEvaluator;
        _logger = logger;
        IsActive = true;
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Sends <see cref="CloseEnvironmentEditorMessage"/> so the environment panel closes.</summary>
    [RelayCommand]
    private void CloseEditor() => Messenger.Send(new CloseEnvironmentEditorMessage());

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

    /// <summary>Reverts unsaved changes for the currently selected environment to its last-saved state.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedEnvironment))]
    private void RevertSelected()
    {
        SelectedEnvironment?.Revert();
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
                SelectedEnvironment.MarkSaved(model);
                Messenger.Send(new GlobalEnvironmentChangedMessage(model.Variables));
            }
            else
            {
                await _environmentService.SaveEnvironmentAsync(model, ct).ConfigureAwait(true);
                SelectedEnvironment.MarkSaved(model);
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
        _ = LoadRequestNamesAsync(message.Value);
    }

    // ─── Dynamic variable config (dialog coordination) ────────────────────────

    /// <summary>
    /// Opens the dynamic value configuration dialog for the given segment (null = new config).
    /// Returns the configured segment if the user confirms, or null if cancelled.
    /// Called from variable item ViewModels via their <c>EditDynamicSegmentCallback</c>.
    /// </summary>
    internal Task<DynamicValueSegment?> OpenDynamicValueConfigAsync(
        DynamicValueSegment? existing,
        EnvironmentListItemViewModel? sourceEnv = null)
    {
        _pendingDynamicConfigTcs?.TrySetResult(null);

        // Use the environment that actually owns the variable being edited.
        // Falling back to SelectedEnvironment is a safety net; in practice
        // the callback always supplies sourceEnv.
        var envItem = sourceEnv ?? SelectedEnvironment;
        var model = envItem?.BuildModel();

        // Static variable values (used as the base for substitution before dynamic resolution).
        var staticVars = model?.Variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Name) && v.Segments is not { Count: > 0 })
            .ToDictionary(v => v.Name.Trim(), v => v.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>();

        PendingDynamicConfig = new DynamicValueConfigViewModel(
            _dynamicEvaluator,
            _collectionFolderPath ?? string.Empty,
            envItem?.FilePath ?? string.Empty,
            _availableRequestNames,
            model?.Variables ?? [],
            staticVars,
            existing);

        _pendingDynamicConfigTcs = new TaskCompletionSource<DynamicValueSegment?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ShowDynamicValueConfig = true;
        return _pendingDynamicConfigTcs.Task;
    }

    /// <summary>
    /// Called by the view's code-behind when the dialog closes.
    /// Completes the pending TCS and resets the dialog-open flag.
    /// </summary>
    internal void OnDynamicConfigDialogClosed()
    {
        ShowDynamicValueConfig = false;
        var result = PendingDynamicConfig?.IsConfirmed == true
            ? PendingDynamicConfig.ResultSegment
            : null;
        _pendingDynamicConfigTcs?.TrySetResult(result);
        _pendingDynamicConfigTcs = null;
        PendingDynamicConfig = null;
    }

    /// <summary>
    /// Opens the mock data picker dialog for the given segment (null = new segment).
    /// Returns the configured segment if the user confirms, or null if cancelled.
    /// </summary>
    internal Task<MockDataSegment?> OpenMockDataConfigAsync(MockDataSegment? existing)
    {
        _pendingMockDataConfigTcs?.TrySetResult(null);

        PendingMockDataConfig = new MockDataConfigViewModel(existing);

        _pendingMockDataConfigTcs = new TaskCompletionSource<MockDataSegment?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ShowMockDataConfig = true;
        return _pendingMockDataConfigTcs.Task;
    }

    /// <summary>
    /// Called by the view's code-behind when the mock data dialog closes.
    /// </summary>
    internal void OnMockDataConfigDialogClosed()
    {
        ShowMockDataConfig = false;
        var result = PendingMockDataConfig?.IsConfirmed == true
            ? PendingMockDataConfig.ResultSegment
            : null;
        _pendingMockDataConfigTcs?.TrySetResult(result);
        _pendingMockDataConfigTcs = null;
        PendingMockDataConfig = null;
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

    /// <summary>Saves the current editor list order to the collection's environment folder.</summary>
    private async Task PersistCurrentOrderAsync()
    {
        if (_collectionFolderPath is null) return;

        var orderedNames = Environments
            .Where(e => !e.IsGlobal)              // global env is always pinned — never in the order list
            .Select(e => Path.GetFileName(e.FilePath))
            .Where(n => !string.IsNullOrEmpty(n)) // exclude unsaved clone ghosts
            .ToList<string>();

        try
        {
            await _environmentService
                .SaveEnvironmentOrderAsync(_collectionFolderPath, orderedNames)
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

            await Task.WhenAll(listTask, globalTask).ConfigureAwait(true);

            var list = listTask.Result;
            var globalModel = globalTask.Result;

            Environments.Clear();

            // Global env is always pinned first.
            Environments.Add(CreateListItem(globalModel, isGlobal: true));

            // ListEnvironmentsAsync already returns environments in the saved order
            // (from environment/_order.json), so no additional sorting needed here.
            foreach (var model in list)
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

    private EnvironmentListItemViewModel CreateListItem(EnvironmentModel model, bool isGlobal = false)
    {
        var item = new EnvironmentListItemViewModel(
            model,
            onRenameCommit: RenameAsync,
            onDeleteRequest: (i, ct) => { BeginDelete(i); return Task.CompletedTask; },
            onCloneRequest: (i, ct) => CloneImmediateAsync(i, ct),
            isGlobal: isGlobal);

        // Capture `item` so the dialog receives variables from this specific environment,
        // not whatever is currently selected in the list.
        item.EditDynamicSegmentCallback = seg => OpenDynamicValueConfigAsync(seg, item);
        item.EditMockDataSegmentCallback = seg => OpenMockDataConfigAsync(seg);
        return item;
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

    /// <summary>
    /// Loads all request display names (slash-separated folder paths) from the
    /// collection folder and stores them for the dynamic value config dialog dropdown.
    /// </summary>
    private async Task LoadRequestNamesAsync(string collectionFolderPath)
    {
        try
        {
            var root = await _collectionService.OpenFolderAsync(collectionFolderPath)
                           .ConfigureAwait(false);
            _availableRequestNames = CollectRequestNames(root, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load request names for dynamic variable config");
            _availableRequestNames = [];
        }
    }

    private static List<string> CollectRequestNames(
        Callsmith.Core.Models.CollectionFolder folder, string prefix)
    {
        var names = new List<string>();
        foreach (var req in folder.Requests)
        {
            names.Add(string.IsNullOrEmpty(prefix) ? req.Name : $"{prefix}/{req.Name}");
        }
        foreach (var sub in folder.SubFolders)
        {
            var subPrefix = string.IsNullOrEmpty(prefix) ? sub.Name : $"{prefix}/{sub.Name}";
            names.AddRange(CollectRequestNames(sub, subPrefix));
        }
        return names;
    }
}
