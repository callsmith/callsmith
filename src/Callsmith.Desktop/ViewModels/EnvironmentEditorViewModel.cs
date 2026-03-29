using System.Collections.ObjectModel;
using System.IO;
using Callsmith.Core.Abstractions;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Controls;
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
    IRecipient<CollectionOpenedMessage>,
    IRecipient<RequestRenamedMessage>,
    IRecipient<EnvironmentChangedMessage>,
    IRecipient<CloseEnvironmentEditorMessage>
{
    private readonly IEnvironmentService _environmentService;
    private readonly ICollectionService _collectionService;
    private readonly IDynamicVariableEvaluator _dynamicEvaluator;
    private readonly IEnvironmentMergeService _mergeService;
    private readonly ILogger<EnvironmentEditorViewModel> _logger;

    private string? _collectionFolderPath;
    private string? _activeEnvironmentFilePath;
    // True once the editor has synced SelectedEnvironment to the active environment on initial
    // collection load. After that the user owns the selection and EnvironmentChangedMessage
    // broadcasts (e.g. re-broadcasts triggered by a save) must not override it.
    private bool _hasInitializedEditorSelection;
    private List<string> _availableRequestNames = [];
    private TaskCompletionSource<EnvironmentVariable?>? _pendingMockDataTcs;
    private TaskCompletionSource<EnvironmentVariable?>? _pendingResponseBodyTcs;
    private CancellationTokenSource? _dynPreviewCts;
    private bool _syncingGlobalPreviewSelection;
    // Guard: true while LoadEnvironmentsAsync is restoring saved state from disk.
    // Prevents changes driven by the load (e.g. restoring GlobalPreviewEnvironmentName)
    // from marking environments dirty before the user has touched anything.
    private bool _loadingEnvironments;
    private const string MaskedSecretValue = "\u2022\u2022\u2022\u2022\u2022";

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
    /// The pending mock-data picker ViewModel. Non-null while <see cref="ShowMockDataConfig"/> is true.
    /// </summary>
    public MockDataConfigViewModel? PendingMockDataConfig { get; private set; }

    /// <summary>
    /// Setting this to true signals the view to open the mock-data picker dialog.
    /// </summary>
    [ObservableProperty]
    private bool _showMockDataConfig;

    /// <summary>
    /// The pending response-body config ViewModel. Non-null while <see cref="ShowResponseBodyConfig"/> is true.
    /// </summary>
    public DynamicValueConfigViewModel? PendingResponseBodyConfig { get; private set; }

    /// <summary>
    /// Setting this to true signals the view to open the response-body config dialog.
    /// </summary>
    [ObservableProperty]
    private bool _showResponseBodyConfig;

    /// <summary>
    /// The concrete environment selected as the shared preview context for all global dynamic
    /// variables. When set, all global response-body variable previews are resolved using this
    /// environment's static vars (e.g. baseUrl, credentials) so every var resolves consistently.
    /// Null means fall back to the first available concrete environment.
    /// </summary>
    [ObservableProperty]
    private EnvironmentListItemViewModel? _selectedGlobalPreviewEnvironment;

    /// <summary>All non-global environments available for selection as the global preview context.</summary>
    public IEnumerable<EnvironmentListItemViewModel> GlobalPreviewEnvironments =>
        Environments.Where(e => !e.IsGlobal);

    /// <summary>True when there are non-global environments available for preview selection.</summary>
    public bool HasGlobalPreviewEnvironments => GlobalPreviewEnvironments.Any();

    // ─── Constructor ─────────────────────────────────────────────────────────

    public EnvironmentEditorViewModel(
        IEnvironmentService environmentService,
        ICollectionService collectionService,
        IDynamicVariableEvaluator dynamicEvaluator,
        IMessenger messenger,
        ILogger<EnvironmentEditorViewModel> logger,
        IEnvironmentMergeService? mergeService = null)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(environmentService);
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(dynamicEvaluator);
        ArgumentNullException.ThrowIfNull(logger);
        _environmentService = environmentService;
        _collectionService = collectionService;
        _dynamicEvaluator = dynamicEvaluator;
        _mergeService = mergeService ?? new EnvironmentMergeService(dynamicEvaluator);
        _logger = logger;
        IsActive = true;

        // Re-expose GlobalPreviewEnvironments whenever the Environments list changes
        // (collection is initially empty; environments load asynchronously on open).
        Environments.CollectionChanged += (_, _) => {
            OnPropertyChanged(nameof(GlobalPreviewEnvironments));
            OnPropertyChanged(nameof(HasGlobalPreviewEnvironments));
        };
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Sends <see cref="CloseEnvironmentEditorMessage"/> so the environment panel closes.</summary>
    [RelayCommand]
    private void CloseEditor()
    {
        // Closing the editor ends the current "user-owned" selection session.
        // The next EnvironmentChangedMessage should be allowed to re-initialize selection.
        _hasInitializedEditorSelection = false;
        Messenger.Send(new CloseEnvironmentEditorMessage());
    }

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
        if (SelectedEnvironment is null) return;
        SelectedEnvironment.Revert();

        // If the global env was reverted, also restore the saved preview env selection and
        // re-run the preview refresh. Revert() only reloads the variable list from the backing
        // model and does not update SelectedGlobalPreviewEnvironment or the async preview state.
        if (!SelectedEnvironment.IsGlobal) return;

        var persistedPreviewName = SelectedEnvironment.BuildModel().GlobalPreviewEnvironmentName;
        _syncingGlobalPreviewSelection = true;
        try
        {
            SelectedGlobalPreviewEnvironment = !string.IsNullOrWhiteSpace(persistedPreviewName)
                ? Environments.FirstOrDefault(e => !e.IsGlobal
                    && string.Equals(e.Name, persistedPreviewName, StringComparison.OrdinalIgnoreCase))
                : Environments.FirstOrDefault(e => !e.IsGlobal);
        }
        finally
        {
            _syncingGlobalPreviewSelection = false;
        }

        _dynPreviewCts?.Cancel();
        _dynPreviewCts = new CancellationTokenSource();
        _ = RefreshDynamicPreviewsAsync(SelectedEnvironment, _dynPreviewCts.Token);
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
                // Inject the current preview-env selection so it persists across sessions.
                var modelWithPreview = model with
                {
                    GlobalPreviewEnvironmentName = SelectedGlobalPreviewEnvironment?.Name,
                };
                // Global environment: persist via the dedicated global save path and broadcast.
                await _environmentService.SaveGlobalEnvironmentAsync(modelWithPreview, ct).ConfigureAwait(true);
                SelectedEnvironment.MarkSaved(modelWithPreview);
                Messenger.Send(new GlobalEnvironmentChangedMessage(modelWithPreview));
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
        _hasInitializedEditorSelection = false;
        _ = LoadEnvironmentsAsync(message.Value);
        _ = LoadRequestNamesAsync(message.Value);
    }

    /// <inheritdoc/>
    public void Receive(EnvironmentChangedMessage message)
    {
        _activeEnvironmentFilePath = message.Value?.FilePath;

        // After the initial collection load the user owns the editor selection.
        // Re-broadcasts caused by saves or reloads must not override it.
        if (_hasInitializedEditorSelection || string.IsNullOrEmpty(_activeEnvironmentFilePath))
            return;

        var matchingEnvironment = Environments.FirstOrDefault(e =>
            !e.IsGlobal &&
            string.Equals(e.FilePath, _activeEnvironmentFilePath, StringComparison.OrdinalIgnoreCase));

        if (matchingEnvironment is not null)
        {
            SelectedEnvironment = matchingEnvironment;
            _hasInitializedEditorSelection = true;
        }
    }

    /// <inheritdoc/>
    public void Receive(CloseEnvironmentEditorMessage message)
    {
        // Handle close requests from other ViewModels (e.g. toolbar back button)
        // so the next editor open can re-sync to the currently active environment.
        _hasInitializedEditorSelection = false;
    }

    /// <summary>
    /// Called when a request is renamed. Updates the request selector list
    /// and updates any response-body variables that referenced the old request name.
    /// </summary>
    public void Receive(RequestRenamedMessage message)
    {
        if (string.IsNullOrEmpty(_collectionFolderPath))
            return;

        var oldRequestName = ConvertRequestPathToRequestName(_collectionFolderPath, message.OldFilePath);
        var newRequestName = ConvertRequestPathToRequestName(_collectionFolderPath, message.Renamed.FilePath);

        if (string.IsNullOrEmpty(oldRequestName) || string.IsNullOrEmpty(newRequestName))
            return;

        _ = LoadRequestNamesAsync(_collectionFolderPath);

        if (PendingResponseBodyConfig is not null)
        {
            var index = PendingResponseBodyConfig.AvailableRequests
                .IndexOf(oldRequestName);
            if (index >= 0)
                PendingResponseBodyConfig.AvailableRequests[index] = newRequestName;

            if (string.Equals(PendingResponseBodyConfig.SelectedRequest, oldRequestName, StringComparison.OrdinalIgnoreCase))
                PendingResponseBodyConfig.SelectedRequest = newRequestName;
        }

        var updatedEnvironments = new List<EnvironmentListItemViewModel>();

        foreach (var environment in Environments)
        {
            var anyUpdated = false;
            foreach (var variable in environment.Variables)
            {
                if (string.IsNullOrEmpty(variable.ResponseRequestName))
                    continue;

                if (string.Equals(variable.ResponseRequestName, oldRequestName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetLeafName(variable.ResponseRequestName), GetLeafName(oldRequestName), StringComparison.OrdinalIgnoreCase))
                {
                    variable.ResponseRequestName = newRequestName;
                    anyUpdated = true;
                }
            }

            if (anyUpdated)
            {
                environment.IsDirty = true;
                foreach (var variable in environment.Variables)
                    variable.NotifyPreviewChanged();

                updatedEnvironments.Add(environment);
            }
        }

        if (updatedEnvironments.Count > 0)
            _ = PersistRenamedRequestReferencesAsync(updatedEnvironments);
    }

    private async Task PersistRenamedRequestReferencesAsync(
        IReadOnlyList<EnvironmentListItemViewModel> updatedEnvironments)
    {
        foreach (var environment in updatedEnvironments)
        {
            try
            {
                var model = environment.BuildModel();
                if (environment.IsGlobal)
                {
                    var modelWithPreview = model with
                    {
                        GlobalPreviewEnvironmentName = SelectedGlobalPreviewEnvironment?.Name,
                    };

                    await _environmentService
                        .SaveGlobalEnvironmentAsync(modelWithPreview, CancellationToken.None)
                        .ConfigureAwait(true);

                    environment.MarkSaved(modelWithPreview);
                    Messenger.Send(new GlobalEnvironmentChangedMessage(modelWithPreview));
                }
                else
                {
                    await _environmentService
                        .SaveEnvironmentAsync(model, CancellationToken.None)
                        .ConfigureAwait(true);

                    environment.MarkSaved(model);
                    Messenger.Send(new EnvironmentSavedMessage(model));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to auto-save renamed request references in environment '{Name}'",
                    environment.Name);
                environment.IsDirty = true;
            }
        }
    }

    // ─── Dynamic variable config (dialog coordination) ────────────────────────

    /// <summary>
    /// Opens the mock-data picker for the given variable (null = new variable with default state).
    /// Returns an updated EnvironmentVariable with the new mock-data config, or null if cancelled.
    /// </summary>
    internal Task<EnvironmentVariable?> OpenMockDataConfigAsync(EnvironmentVariable? existing)
    {
        _pendingMockDataTcs?.TrySetResult(null);

        var mockDataSegment = existing?.VariableType == EnvironmentVariable.VariableTypes.MockData
            ? new MockDataSegment
              {
                  Category = existing.MockDataCategory ?? "Internet",
                  Field = existing.MockDataField ?? "Email",
              }
            : null;

        PendingMockDataConfig = new MockDataConfigViewModel(mockDataSegment);

        _pendingMockDataTcs = new TaskCompletionSource<EnvironmentVariable?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ShowMockDataConfig = true;
        return _pendingMockDataTcs.Task;
    }

    /// <summary>Called by the view's code-behind when the mock-data dialog closes.</summary>
    internal void OnMockDataConfigDialogClosed()
    {
        ShowMockDataConfig = false;
        EnvironmentVariable? result = null;
        if (PendingMockDataConfig?.IsConfirmed == true && PendingMockDataConfig.ResultSegment is { } seg)
        {
            result = new EnvironmentVariable
            {
                Name = string.Empty, // caller sets the name
                Value = string.Empty,
                VariableType = EnvironmentVariable.VariableTypes.MockData,
                MockDataCategory = seg.Category,
                MockDataField = seg.Field,
            };
        }
        _pendingMockDataTcs?.TrySetResult(result);
        _pendingMockDataTcs = null;
        PendingMockDataConfig = null;
    }

    /// <summary>
    /// Opens the response-body config dialog for the given variable (null = new variable).
    /// Returns an updated EnvironmentVariable with the new config, or null if cancelled.
    /// </summary>
    internal Task<EnvironmentVariable?> OpenResponseBodyConfigAsync(
        EnvironmentVariable? existing,
        EnvironmentListItemViewModel? sourceEnv = null)
    {
        _pendingResponseBodyTcs?.TrySetResult(null);

        var envItem = sourceEnv ?? SelectedEnvironment;
        var model = envItem?.BuildModel();
        var isGlobal = envItem?.IsGlobal == true;

        // Build static vars baseline: start with global static vars (if this is a non-global env),
        // then layer the active env's static vars on top. Matches send-time precedence so that
        // tokens like {{access-token-header}} from the global env resolve inside preview requests.
        Dictionary<string, string> staticVars;
        if (!isGlobal)
        {
            staticVars = new Dictionary<string, string>(StringComparer.Ordinal);
            var globalItem = Environments.FirstOrDefault(e => e.IsGlobal);
            if (globalItem is not null)
            {
                foreach (var v in globalItem.BuildModel().Variables
                    .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.Static
                             && !string.IsNullOrWhiteSpace(v.Name)))
                    staticVars[v.Name.Trim()] = v.Value;
            }
            if (model is not null)
            {
                foreach (var v in model.Variables
                    .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.Static
                             && !string.IsNullOrWhiteSpace(v.Name)))
                    staticVars[v.Name.Trim()] = v.Value;
            }
        }
        else
        {
            staticVars = model?.Variables
                .Where(v => !string.IsNullOrWhiteSpace(v.Name)
                    && v.VariableType == EnvironmentVariable.VariableTypes.Static)
                .ToDictionary(v => v.Name.Trim(), v => v.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // Re-use DynamicValueConfigViewModel for the response-body picker dialog.
        DynamicValueSegment? existingSegment = null;
        if (existing?.VariableType == EnvironmentVariable.VariableTypes.ResponseBody
            && existing.ResponseRequestName is not null)
        {
            existingSegment = new DynamicValueSegment
            {
                RequestName = existing.ResponseRequestName,
                Path = existing.ResponsePath ?? string.Empty,
                Matcher = existing.ResponseMatcher,
                Frequency = existing.ResponseFrequency,
                ExpiresAfterSeconds = existing.ResponseExpiresAfterSeconds,
            };
        }

        // When editing a global-environment dynamic var, pre-populate the static vars
        // with the SelectedGlobalPreviewEnvironment so the "Test" button uses the same
        // context as the passive preview column.
        if (isGlobal && SelectedGlobalPreviewEnvironment is { } envLevelChoice)
        {
            foreach (var v in envLevelChoice.BuildModel().Variables
                .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.Static
                         && !string.IsNullOrWhiteSpace(v.Name)))
                staticVars[v.Name.Trim()] = v.Value;
        }

        // Compute the cache namespace for the config VM.
        // For global env vars, use the same env-scoped namespace as the passive preview and
        // send-time evaluation (globalId[env:previewId]) so that token cache entries written
        // by "Send" are found by the "Test" button and vice versa.
        // Switching the preview env therefore correctly invalidates any stale token.
        string configCacheNamespace;
        if (isGlobal && SelectedGlobalPreviewEnvironment is { } previewChoice && envItem is not null)
            configCacheNamespace = $"{envItem.EnvironmentId:N}[env:{previewChoice.EnvironmentId:N}]";
        else
            configCacheNamespace = envItem?.EnvironmentId.ToString("N") ?? string.Empty;

        // For non-global envs, supply the global env's variables so that PreviewAsync
        // can pre-resolve global dynamic vars (e.g. `token`) before applying the
        // active env's own dynamic vars — same two-phase logic as send time.
        IReadOnlyList<EnvironmentVariable>? globalVars = null;
        string? globalEnvCacheNamespace = null;
        if (!isGlobal)
        {
            var globalItem = Environments.FirstOrDefault(e => e.IsGlobal);
            if (globalItem is not null)
            {
                globalVars = globalItem.BuildModel().Variables;
                globalEnvCacheNamespace = globalItem.EnvironmentId.ToString("N");
            }
        }

        PendingResponseBodyConfig = new DynamicValueConfigViewModel(
            _dynamicEvaluator,
            _collectionFolderPath ?? string.Empty,
            configCacheNamespace,
            _availableRequestNames,
            model?.Variables ?? [],
            staticVars,
            existingSegment,
            globalVariables: globalVars,
            globalEnvironmentCacheNamespace: globalEnvCacheNamespace);

        _pendingResponseBodyTcs = new TaskCompletionSource<EnvironmentVariable?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ShowResponseBodyConfig = true;
        return _pendingResponseBodyTcs.Task;
    }

    /// <summary>Called by the view's code-behind when the response-body config dialog closes.</summary>
    internal void OnResponseBodyConfigDialogClosed()
    {
        ShowResponseBodyConfig = false;
        EnvironmentVariable? result = null;
        if (PendingResponseBodyConfig?.IsConfirmed == true
            && PendingResponseBodyConfig.ResultSegment is { } seg)
        {
            result = new EnvironmentVariable
            {
                Name = string.Empty, // caller sets the name
                Value = string.Empty,
                VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                ResponseRequestName = seg.RequestName,
                ResponsePath = seg.Path,
                ResponseMatcher = seg.Matcher,
                ResponseFrequency = seg.Frequency,
                ResponseExpiresAfterSeconds = seg.ExpiresAfterSeconds,
            };
        }
        _pendingResponseBodyTcs?.TrySetResult(result);
        _pendingResponseBodyTcs = null;
        PendingResponseBodyConfig = null;
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private bool HasSelectedEnvironment => SelectedEnvironment is not null;
    private bool HasSelectedDeletableEnvironment => SelectedEnvironment is { IsGlobal: false };

    /// <summary>
    /// Triggers an async evaluation of all dynamic variables in the newly selected environment
    /// so that static variable previews can display fully-resolved values (e.g. "Bearer {{token}}"
    /// becomes "Bearer eyJ..."). Uses the cache where available; executes HTTP only when needed.
    /// </summary>
    partial void OnSelectedEnvironmentChanged(EnvironmentListItemViewModel? value)
    {
        RefreshSelectedEnvironmentSuggestions();

        _dynPreviewCts?.Cancel();
        _dynPreviewCts = new CancellationTokenSource();
        if (value is not null)
            _ = RefreshDynamicPreviewsAsync(value, _dynPreviewCts.Token);
    }

    partial void OnSelectedGlobalPreviewEnvironmentChanged(EnvironmentListItemViewModel? value)
    {
        if (_syncingGlobalPreviewSelection)
            return;

        var globalEnv = Environments.FirstOrDefault(e => e.IsGlobal);
        if (globalEnv is null) return;

        var persistedPreviewName = globalEnv.BuildModel().GlobalPreviewEnvironmentName;

        // Reordering concrete environments can cause a transient null selection in the
        // preview ComboBox. If the persisted preview environment still exists, restore it
        // and treat this as a non-user change.
        if (value is null && !string.IsNullOrWhiteSpace(persistedPreviewName))
        {
            var persistedItem = Environments
                .FirstOrDefault(e => !e.IsGlobal
                    && string.Equals(e.Name, persistedPreviewName, StringComparison.OrdinalIgnoreCase));

            if (persistedItem is not null)
            {
                _syncingGlobalPreviewSelection = true;
                try
                {
                    SelectedGlobalPreviewEnvironment = persistedItem;
                }
                finally
                {
                    _syncingGlobalPreviewSelection = false;
                }
                return;
            }
        }

        // Only mark dirty if this is a user change, not a programmatic restore during load.
        if (!_loadingEnvironments)
            globalEnv.IsDirty = true;

        // Re-run the global env preview with the newly selected context env.
        if (SelectedEnvironment == globalEnv)
        {
            _dynPreviewCts?.Cancel();
            _dynPreviewCts = new CancellationTokenSource();
            _ = RefreshDynamicPreviewsAsync(globalEnv, _dynPreviewCts.Token);
        }
    }

    private void RefreshSelectedEnvironmentSuggestions()
    {
        if (SelectedEnvironment is null)
            return;

        SelectedEnvironment.SetSuggestions(BuildSuggestionsFor(SelectedEnvironment));
    }

    private IReadOnlyList<EnvVarSuggestion> BuildSuggestionsFor(EnvironmentListItemViewModel env)
    {
        var merged = new Dictionary<string, EnvironmentVariable>(StringComparer.Ordinal);

        if (!env.IsGlobal)
        {
            var global = Environments.FirstOrDefault(item => item.IsGlobal);
            if (global is not null)
            {
                foreach (var variable in global.BuildModel().Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
                    merged[variable.Name.Trim()] = variable;
            }
        }

        foreach (var variable in env.BuildModel().Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
            merged[variable.Name.Trim()] = variable;

        return merged.Values
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new EnvVarSuggestion(v.Name.Trim(), v.IsSecret ? "\u2022\u2022\u2022\u2022\u2022" : v.Value))
            .ToList();
    }

    private void OnEnvironmentVariablesChanged(object? sender, EventArgs e)
    {
        if (SelectedEnvironment is null || sender is not EnvironmentListItemViewModel changed)
            return;

        if (ReferenceEquals(changed, SelectedEnvironment)
            || (!SelectedEnvironment.IsGlobal && changed.IsGlobal))
            RefreshSelectedEnvironmentSuggestions();

        // Re-push conflict info whenever the selected env's own vars change OR when global
        // vars change (e.g. user toggles the override checkbox) regardless of which env is selected.
        if (ReferenceEquals(changed, SelectedEnvironment) || changed.IsGlobal)
            PushConflictInfo(SelectedEnvironment);

        // Re-run the full dynamic preview whenever the global env's variables change (e.g.
        // the force-override checkbox is toggled). That change alters the effective merge for
        // every concrete env, so the preview values must be recalculated.
        if (changed.IsGlobal)
        {
            _dynPreviewCts?.Cancel();
            _dynPreviewCts = new CancellationTokenSource();
            _ = RefreshDynamicPreviewsAsync(SelectedEnvironment, _dynPreviewCts.Token);
        }
    }

    /// <summary>
    /// Resolves all dynamic variables for <paramref name="env"/> and stores the results in it
    /// so that variable substitution previews include response-body and mock-data values.
    /// For non-global environments, also resolves and pushes the global environment context
    /// so that tokens like {{base-url}} and {{token}} resolve in the preview column even when
    /// they live in the global env. Falls back silently on failure.
    /// </summary>
    private async Task RefreshDynamicPreviewsAsync(
        EnvironmentListItemViewModel env, CancellationToken ct)
    {
        if (_collectionFolderPath is null) return;

        var model = env.BuildModel();

        var hasDynamic = model.Variables.Any(v =>
            v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody
            || v.VariableType == EnvironmentVariable.VariableTypes.MockData
            || v.VariableType == EnvironmentVariable.VariableTypes.Dynamic);

        // Whether this env has variables that actually require HTTP to compute.
        // MockData vars are generated client-side and never need a network call.
        var hasResponseBodyVars = model.Variables.Any(v =>
            v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody
            || v.VariableType == EnvironmentVariable.VariableTypes.Dynamic);

        // ── Global env path ──────────────────────────────────────────────────
        if (env.IsGlobal)
        {
            // Use the env-level preview selection when set; otherwise fall back to the first
            // concrete environment. This matches the send-time cache namespace so cached tokens
            // from recent request sends are reused without an extra HTTP call.
            var contextEnv = SelectedGlobalPreviewEnvironment ?? Environments.FirstOrDefault(e => !e.IsGlobal);
            var contextEnvModel = contextEnv?.BuildModel();

            // Build the static-only merged context for SetGlobalPreviewValues. The service uses
            // the same three-layer precedence as the send pipeline:
            //   (1) global statics → (2) context-env statics → (3) force-override global statics.
            var globalStaticVars = _mergeService.BuildStaticMerge(model, contextEnvModel);

            // Track which keys are the global env's OWN static vars so the dynVars filter
            // below only excludes them — not same-named statics from the concrete context env.
            var globalOwnStaticKeys = model.Variables
                .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.Static
                         && !string.IsNullOrWhiteSpace(v.Name))
                .Select(v => v.Name.Trim())
                .ToHashSet(StringComparer.Ordinal);

            if (!hasDynamic)
            {
                // No dynamic vars — still push merged static context so that {{token}}-style
                // references in static var values resolve correctly in the preview column.
                env.SetGlobalPreviewValues(globalStaticVars, new Dictionary<string, MockDataEntry>());

                // Keep global conflict rows honest by refreshing the preview env first so
                // its response-body values are available for OVERRIDES / OVERRIDDEN WITH.
                if (contextEnv is not null)
                    await RefreshDynamicPreviewsAsync(contextEnv, ct).ConfigureAwait(true);

                PushConflictInfo(env);
                return;
            }

            try
            {
                // Resolve the global env's own dynamic vars against the preview context, but keep
                // the row preview tied to the global variable's own resolved value rather than the
                // final post-override merged value. The conflict row separately communicates when a
                // concrete env value wins.
                var globalResolved = await _dynamicEvaluator
                    .ResolveAsync(
                        _collectionFolderPath,
                        BuildGlobalCacheNamespace(model, contextEnvModel),
                        model.Variables,
                        globalStaticVars,
                        ct)
                    .ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();

                // Only exclude the global env's OWN static keys — not same-named statics from
                // the preview context env (those must not suppress response-body preview values).
                var dynVars = globalResolved.Variables
                    .Where(kv => !globalOwnStaticKeys.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                env.SetDynamicPreviewValues(dynVars, globalResolved.MockGenerators);
                // Also push the merged static context so that {{token}}-style references in
                // static var values resolve against the effective (preview-env-aware) merged dict.
                env.SetGlobalPreviewValues(globalStaticVars, globalResolved.MockGenerators);

                // Keep global conflict rows honest by refreshing the preview env first so
                // its response-body values are available for OVERRIDES / OVERRIDDEN WITH.
                if (contextEnv is not null)
                    await RefreshDynamicPreviewsAsync(contextEnv, ct).ConfigureAwait(true);

                PushConflictInfo(env);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Could not refresh dynamic variable previews for environment '{Name}'", env.Name);
                PushConflictInfo(env);
            }
            return;
        }

        // ── Concrete env path ────────────────────────────────────────────────
        // Step 1: Build global context vars (global statics + Phase 1 global dynamics) and
        // push them to this env's preview system. This enables {{base-url}}, {{token}}, etc.
        // in the preview column regardless of whether this env has its own dynamic vars.
        var activeStaticVars = model.Variables
            .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.Static
                     && !string.IsNullOrWhiteSpace(v.Name))
            .ToDictionary(v => v.Name.Trim(), v => v.Value, StringComparer.Ordinal);

        var globalContextVars = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, MockDataEntry> globalMockGenerators
            = new Dictionary<string, MockDataEntry>();
        // Global-only resolved values (no concrete-env statics re-applied on top).
        // Used for conflict info so "OVERRIDDEN BY" shows the global var's own value.
        Dictionary<string, string> pureGlobalContextVars = globalContextVars;
        var globalItem = Environments.FirstOrDefault(e => e.IsGlobal);
        var globalModel = globalItem?.BuildModel()
            ?? new EnvironmentModel { FilePath = string.Empty, Name = "Global", Variables = [], EnvironmentId = Guid.Empty };

        var globalHasResponseBody = globalModel.Variables.Any(v =>
            v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody
            || v.VariableType == EnvironmentVariable.VariableTypes.Dynamic);

        // Resolve global response-body vars whenever the global env has them.
        // Use only the concrete env being viewed as the resolution context — this ensures
        // the preview is honest: if the env is misconfigured (e.g. wrong base URL), the
        // global dynamic vars that depend on it correctly show blank rather than leaking
        // a resolved value from a different, working environment.
        if (globalHasResponseBody)
        {
            try
            {
                var candidateMerge = await _mergeService
                    .MergeAsync(_collectionFolderPath, globalModel, model, ct)
                    .ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();

                foreach (var kv in candidateMerge.Variables)
                    globalContextVars[kv.Key] = kv.Value;
                globalMockGenerators = candidateMerge.MockGenerators;

                // Build conflict values from a global-only dynamic resolve. The merged
                // candidate includes active-env static re-application, which can overwrite
                // same-name global dynamic vars and cause an incorrect OVERRIDES preview.
                var globalStaticContext = _mergeService.BuildStaticMerge(globalModel, model);
                var globalResolvedForConflicts = await _dynamicEvaluator
                    .ResolveAsync(
                        _collectionFolderPath,
                        BuildGlobalCacheNamespace(globalModel, model),
                        globalModel.Variables,
                        globalStaticContext,
                        ct)
                    .ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();

                pureGlobalContextVars = BuildPureGlobalPreviewVars(globalModel, globalResolvedForConflicts.Variables);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Could not resolve global dynamic vars for concrete env '{Name}'", env.Name);
            }

            // Re-apply active env's own statics at the end to maintain override precedence.
            foreach (var kv in activeStaticVars)
                globalContextVars[kv.Key] = kv.Value;
        }
        else
        {
            // No HTTP needed — still collect global mock-data generators so that
            // {{faker-*}} tokens from the global env resolve in the preview column.
            var globalStaticBase = _mergeService.BuildStaticMerge(globalModel, null);
            foreach (var kv in globalStaticBase) globalContextVars[kv.Key] = kv.Value;

            var mockGens = new Dictionary<string, MockDataEntry>(StringComparer.Ordinal);
            foreach (var v in globalModel.Variables
                .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.MockData
                         && !string.IsNullOrWhiteSpace(v.Name)))
            {
                var entry = v.GetMockEntry();
                if (entry is not null)
                    mockGens[v.Name] = entry;
            }
            if (mockGens.Count > 0)
                globalMockGenerators = mockGens;

            // No activeStaticVars overlay in this branch — but still build pureGlobalContextVars
            // explicitly from the global model's static values rather than reusing the
            // globalContextVars reference, so that any future modification cannot contaminate it.
            pureGlobalContextVars = BuildPureGlobalPreviewVars(globalModel, globalContextVars);
        }

        // Push global context: {{base-url}}, {{token}}, {{faker-*}}, etc. now resolve
        // in the preview column regardless of whether this env has its own dynamic vars.
        env.SetGlobalPreviewValues(globalContextVars, globalMockGenerators);
        // Store pure global values (before concrete-env statics overlay) for conflict display.
        env.SetPureGlobalPreviewVars(pureGlobalContextVars);

        // Push conflict info using the pure global context vars so that
        // "OVERRIDDEN BY" on concrete vars shows the global env's resolved value.
        PushConflictInfoForConcreteEnv(env, pureGlobalContextVars);

        if (!hasDynamic) return; // No dynamic vars in this env — global context above is sufficient.

        // Step 2: Resolve this env's own dynamic vars from the pre-final-override context so
        // PREVIEW reflects concrete-env evaluation, while the conflict row communicates when
        // a force-override global var wins at send time.
        try
        {
            var activeResolveContext = new Dictionary<string, string>(pureGlobalContextVars, StringComparer.Ordinal);
            foreach (var kv in activeStaticVars)
                activeResolveContext[kv.Key] = kv.Value;

            var activeResolved = await _dynamicEvaluator
                .ResolveAsync(
                    _collectionFolderPath,
                    model.EnvironmentId.ToString("N"),
                    model.Variables,
                    activeResolveContext,
                    ct)
                .ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            // Extract only the active env's own dynamic var values.
            var activeDynVarNames = model.Variables
                .Where(v => v.VariableType != EnvironmentVariable.VariableTypes.Static
                         && !string.IsNullOrWhiteSpace(v.Name))
                .Select(v => v.Name.Trim())
                .ToHashSet(StringComparer.Ordinal);

            var dynVars = activeResolved.Variables
                .Where(kv => activeDynVarNames.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            env.SetDynamicPreviewValues(dynVars, activeResolved.MockGenerators);
        }
        catch (OperationCanceledException)
        {
            // Environment selection changed — preview refresh superseded.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Could not refresh dynamic variable previews for environment '{Name}'", env.Name);
        }
    }

    private static Dictionary<string, string> BuildPureGlobalPreviewVars(
        EnvironmentModel globalModel,
        IReadOnlyDictionary<string, string> resolvedGlobalVars)
    {
        var pureGlobalContextVars = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var globalVariable in globalModel.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            var key = globalVariable.Name.Trim();

            if (globalVariable.VariableType == EnvironmentVariable.VariableTypes.Static)
            {
                pureGlobalContextVars[key] = globalVariable.Value;
                continue;
            }

            if (globalVariable.VariableType == EnvironmentVariable.VariableTypes.MockData)
            {
                var entry = globalVariable.GetMockEntry();
                if (entry is not null)
                    pureGlobalContextVars[key] = MockDataCatalog.Generate(entry.Category, entry.Field);

                continue;
            }

            if (resolvedGlobalVars.TryGetValue(key, out var resolvedValue))
                pureGlobalContextVars[key] = resolvedValue;
        }

        return pureGlobalContextVars;
    }

    private static string BuildGlobalCacheNamespace(EnvironmentModel globalEnv, EnvironmentModel? contextEnv)
    {
        return contextEnv is not null
            ? $"{globalEnv.EnvironmentId:N}[env:{contextEnv.EnvironmentId:N}]"
            : globalEnv.EnvironmentId.ToString("N");
    }

    /// <summary>
    /// Pushes conflict info (OVERRIDES / OVERRIDDEN BY) to each variable row in <paramref name="env"/>.
    /// </summary>
    private void PushConflictInfo(EnvironmentListItemViewModel env)
    {
        if (env.IsGlobal)
        {
            // For global env: compare each var against the "preview against" concrete env.
            var previewEnv = SelectedGlobalPreviewEnvironment ?? Environments.FirstOrDefault(e => !e.IsGlobal);
            if (previewEnv is null)
            {
                env.SetConflictValues(new Dictionary<string, (string, string, string)>());
                return;
            }

            var concreteVars = previewEnv.BuildModel().Variables
                .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                .ToDictionary(
                    v => v.Name.Trim(),
                    v =>
                    {
                        var key = v.Name.Trim();
                        var value = previewEnv.TryGetResolvedPreviewValue(key, out var resolvedPreview)
                            ? resolvedPreview
                            : v.Value;
                        return (value, v.IsSecret);
                    },
                    StringComparer.Ordinal);

            var conflicts = new Dictionary<string, (string label, string value, string toolTip)>(StringComparer.Ordinal);
            foreach (var v in env.BuildModel().Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
            {
                var key = v.Name.Trim();
                if (concreteVars.TryGetValue(key, out var concreteVar))
                {
                    var label = v.IsForceGlobalOverride ? "OVERRIDES" : "OVERRIDDEN WITH";
                    var toolTip = v.IsForceGlobalOverride
                        ? "Overrides this value when used in requests in the previewed environment"
                        : "Overridden with this value when used in requests in the previewed environment";
                    conflicts[key] = (label, concreteVar.IsSecret ? MaskedSecretValue : concreteVar.value, toolTip);
                }
            }

            env.SetConflictValues(conflicts);
        }
        else
        {
            // For concrete env: use the pre-resolved global preview vars (already cached on the env).
            PushConflictInfoForConcreteEnv(env, env.GetResolvedGlobalPreviewVars());
        }
    }

    /// <summary>
    /// Pushes conflict info to a concrete env's variable rows, using
    /// <paramref name="resolvedGlobalVars"/> as the source of the global env's resolved values.
    /// Matching vars show either "OVERRIDDEN WITH" (global Override enabled) or
    /// "OVERRIDES" (global Override disabled).
    /// </summary>
    private void PushConflictInfoForConcreteEnv(
        EnvironmentListItemViewModel env,
        IReadOnlyDictionary<string, string> resolvedGlobalVars)
    {
        var globalItem = Environments.FirstOrDefault(e => e.IsGlobal);
        if (globalItem is null)
        {
            env.SetConflictValues(new Dictionary<string, (string, string, string)>());
            return;
        }

        var globalVarsByName = globalItem.BuildModel().Variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .ToDictionary(v => v.Name.Trim(), v => v, StringComparer.Ordinal);

        if (globalVarsByName.Count == 0)
        {
            env.SetConflictValues(new Dictionary<string, (string, string, string)>());
            return;
        }

        var conflicts = new Dictionary<string, (string label, string value, string toolTip)>(StringComparer.Ordinal);
        foreach (var v in env.BuildModel().Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            var key = v.Name.Trim();
            if (!globalVarsByName.TryGetValue(key, out var globalVar))
                continue;

            if (globalVar.IsForceGlobalOverride)
            {
                if (globalVar.IsSecret)
                {
                    conflicts[key] = ("OVERRIDDEN WITH", MaskedSecretValue, "Overridden by a secret global variable");
                    continue;
                }

                if (resolvedGlobalVars.TryGetValue(key, out var globalValue))
                    conflicts[key] = ("OVERRIDDEN WITH", globalValue, "Overridden with this value by a global variable");

                continue;
            }

            if (globalVar.IsSecret)
            {
                conflicts[key] = ("OVERRIDES", MaskedSecretValue, "Overrides a secret global variable");
                continue;
            }

            if (resolvedGlobalVars.TryGetValue(key, out var globalPreviewValue))
                conflicts[key] = ("OVERRIDES", globalPreviewValue, "Overrides this global variable value");
        }

        env.SetConflictValues(conflicts);
    }

    /// <summary>
    /// Moves <paramref name="item"/> to <paramref name="targetIndex"/> in the
    /// <see cref="Environments"/> list immediately during drag.
    /// Returns <see langword="true"/> when the order changed.
    /// </summary>
    public bool MoveEnvironment(EnvironmentListItemViewModel item, int targetIndex)
    {
        var currentIndex = Environments.IndexOf(item);
        if (currentIndex < 0 || currentIndex == targetIndex) return false;
        // Index 0 is always the pinned Global env — never move to or from it.
        if (currentIndex == 0 || targetIndex <= 0 || targetIndex >= Environments.Count) return false;

        Environments.Move(currentIndex, targetIndex);

        return true;
    }

    /// <summary>
    /// Persists the current environment order after a drag completes and refreshes the selector.
    /// </summary>
    public async Task PersistEnvironmentOrderAsync()
    {
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

            // Restore the saved global preview environment selection without dirtying anything.
            _loadingEnvironments = true;
            try
            {
                if (globalModel.GlobalPreviewEnvironmentName is { } savedName)
                    SelectedGlobalPreviewEnvironment = Environments
                        .FirstOrDefault(e => !e.IsGlobal
                            && string.Equals(e.Name, savedName, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _loadingEnvironments = false;
            }

            // Select the active environment when available; otherwise keep the prior
            // behavior of selecting the first non-global environment.
            var activeMatch = !string.IsNullOrEmpty(_activeEnvironmentFilePath)
                ? Environments.FirstOrDefault(e =>
                    !e.IsGlobal &&
                    string.Equals(e.FilePath, _activeEnvironmentFilePath, StringComparison.OrdinalIgnoreCase))
                : null;

            SelectedEnvironment = activeMatch ?? (Environments.Count > 1 ? Environments[1] : Environments[0]);

            // Mark the initial selection done so that subsequent EnvironmentChangedMessage
            // broadcasts (e.g. re-broadcasts triggered by a save reload) do not override
            // the user's selection.  Only mark done when we actually matched the active env;
            // if we fell back to the first item, a later message may still legitimately correct
            // the selection before the user has taken any action.
            if (activeMatch is not null)
                _hasInitializedEditorSelection = true;

            // Broadcast the current global vars so request tabs are up-to-date.
            Messenger.Send(new GlobalEnvironmentChangedMessage(globalModel));
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
            isGlobal: isGlobal,
            collectionFolderPath: _collectionFolderPath ?? "");

        item.VariablesChanged += OnEnvironmentVariablesChanged;

        item.EditMockDataCallback = v => OpenMockDataConfigAsync(v);
        item.EditResponseBodyCallback = v => OpenResponseBodyConfigAsync(v, item);
        return item;
    }

    private async Task RenameAsync(
        EnvironmentListItemViewModel item, string newName, CancellationToken ct)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var oldFilePath = item.FilePath;
            var renamedModel = await _environmentService
                .RenameEnvironmentAsync(item.FilePath, newName, ct)
                .ConfigureAwait(true);

            item.ApplyRename(renamedModel);

            // Notify EnvironmentViewModel first so it can update its active-environment
            // reference (and re-persist prefs) before the order-reload fires below.
            Messenger.Send(new EnvironmentRenamedMessage(oldFilePath, renamedModel));

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

    private static string GetLeafName(string requestName)
    {
        var slash = requestName.LastIndexOf('/');
        return slash >= 0 ? requestName[(slash + 1)..] : requestName;
    }

    private static string ConvertRequestPathToRequestName(string collectionFolderPath, string requestFilePath)
    {
        if (string.IsNullOrWhiteSpace(collectionFolderPath) || string.IsNullOrWhiteSpace(requestFilePath))
            return string.Empty;

        var relative = Path.GetRelativePath(collectionFolderPath, requestFilePath);
        var withoutExt = Path.ChangeExtension(relative, null)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(withoutExt))
            return string.Empty;

        return withoutExt.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
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
