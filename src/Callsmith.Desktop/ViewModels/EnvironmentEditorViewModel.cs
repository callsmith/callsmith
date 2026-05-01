using System.Collections.ObjectModel;
using System.IO;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Bruno;
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
    IRecipient<OpenEnvironmentEditorMessage>
{
    private readonly IEnvironmentService _environmentService;
    private readonly ICollectionService _collectionService;
    private readonly IDynamicVariableEvaluator _dynamicEvaluator;
    private readonly IEnvironmentMergeService _mergeService;
    private readonly IEnvironmentVariableSuggestionService _environmentVariableSuggestionService;
    private readonly ILogger<EnvironmentEditorViewModel> _logger;
    private readonly IUndoRedoService? _undoRedoService;

    private string? _collectionFolderPath;
    private bool _hasPendingOpenEditorSelection;
    private string? _pendingOpenEditorEnvironmentFilePath;
    private List<string> _availableRequestNames = [];
    private TaskCompletionSource<EnvironmentVariable?>? _pendingMockDataTcs;
    private TaskCompletionSource<EnvironmentVariable?>? _pendingResponseBodyTcs;
    private CancellationTokenSource? _dynPreviewCts;
    private bool _syncingGlobalPreviewSelection;
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

    /// <summary>
    /// True if the currently selected environment is a non-global Bruno environment.
    /// </summary>
    [ObservableProperty]
    private bool _isBrunoConcreteEnvironment;

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
        IEnvironmentVariableSuggestionService? environmentVariableSuggestionService = null,
        IEnvironmentMergeService? mergeService = null,
        IUndoRedoService? undoRedoService = null)
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
        _environmentVariableSuggestionService = environmentVariableSuggestionService ?? new EnvironmentVariableSuggestionService();
        _logger = logger;
        _undoRedoService = undoRedoService;
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

    /// <summary>Saves all dirty environments to disk (Ctrl+Shift+S).</summary>
    [RelayCommand]
    private async Task SaveAllEnvironmentsAsync(CancellationToken ct)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var dirtyItems = Environments.Where(e => e.IsDirty).ToList();
            if (dirtyItems.Count == 0) return;

            // Build all models upfront (applying global preview env name for the global env).
            var entries = dirtyItems.Select(env =>
            {
                var model = env.BuildModel();
                if (env.IsGlobal)
                    model = model with { GlobalPreviewEnvironmentName = SelectedGlobalPreviewEnvironment?.Name };
                return (Vm: env, Model: model);
            }).ToList();

            // Save all environments in a single batch call.
            // For Callsmith collections this writes secrets in one read-modify-write on the
            // backing store (instead of once per env), then writes each env file atomically.
            // For Bruno collections this delegates to sequential per-env saves.
            await _environmentService
                .SaveEnvironmentsAsync(entries.Select(e => e.Model).ToList(), ct)
                .ConfigureAwait(true);

            // Update dirty state and notify subscribers.
            foreach (var (vm, model) in entries)
            {
                vm.MarkSaved(model);
                if (vm.IsGlobal)
                    Messenger.Send(new GlobalEnvironmentChangedMessage(model));
                else
                    Messenger.Send(new EnvironmentSavedMessage(model));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save all environments");
            ErrorMessage = "Failed to save all environments. Check logs for details.";
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

    /// <inheritdoc/>
    public void Receive(OpenEnvironmentEditorMessage message)
    {
        var activeEnvironmentFilePath = message.Value;
        if (Environments.Count == 0)
        {
            _hasPendingOpenEditorSelection = true;
            _pendingOpenEditorEnvironmentFilePath = activeEnvironmentFilePath;
            return;
        }

        ApplyManagerOpenSelection(activeEnvironmentFilePath);
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
        IReadOnlyList<EnvironmentListItemViewModel> updatedEnvironments,
        CancellationToken ct = default)
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
                        .SaveGlobalEnvironmentAsync(modelWithPreview, ct)
                        .ConfigureAwait(true);

                    environment.MarkSaved(modelWithPreview);
                    Messenger.Send(new GlobalEnvironmentChangedMessage(modelWithPreview));
                }
                else
                {
                    await _environmentService
                        .SaveEnvironmentAsync(model, ct)
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
        // For global env vars, use the preview environment's ID so that cache entries written
        // by "Send" are found by the "Test" button and vice versa (unified cache namespace).
        string configCacheNamespace;
        if (isGlobal && SelectedGlobalPreviewEnvironment is { } previewChoice)
            configCacheNamespace = previewChoice.EnvironmentId.ToString("N");
        else
            configCacheNamespace = envItem?.EnvironmentId.ToString("N") ?? string.Empty;

        // For non-global envs, supply the global env's variables so that PreviewAsync
        // can pre-resolve global dynamic vars (e.g. `token`) before applying the
        // active env's own dynamic vars — same two-phase logic as send time.
        // Use the active env's own cache namespace for global vars (unified namespace).
        IReadOnlyList<EnvironmentVariable>? globalVars = null;
        string? globalEnvCacheNamespace = null;
        if (!isGlobal)
        {
            var globalItem = Environments.FirstOrDefault(e => e.IsGlobal);
            if (globalItem is not null)
            {
                globalVars = globalItem.BuildModel().Variables;
                // Use the active env's namespace so the global resolution shares the same
                // cache as send-time and the editor preview (unified cache namespace).
                globalEnvCacheNamespace = envItem?.EnvironmentId.ToString("N");
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

    /// <summary>
    /// Selects the environment with the given <paramref name="environmentId"/> in the editor list.
    /// Does nothing when no matching environment is found.
    /// Called by <see cref="MainWindowViewModel"/> during undo/redo navigation.
    /// </summary>
    internal void SelectEnvironmentById(Guid environmentId)
    {
        var env = Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        if (env is not null)
            SelectedEnvironment = env;
    }

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

        // Update if this is a Bruno Concrete Environment
        IsBrunoConcreteEnvironment =
            value != null && 
            !value.IsGlobal &&
            !string.IsNullOrEmpty(_collectionFolderPath) && 
            BrunoDetector.IsBrunoCollection(_collectionFolderPath);
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

        globalEnv.SetGlobalPreviewEnvironmentName(value?.Name);

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
        IEnumerable<EnvironmentVariable>? globalVars = null;
        if (!env.IsGlobal)
        {
            var global = Environments.FirstOrDefault(item => item.IsGlobal);
            if (global is not null)
                globalVars = global.BuildModel().Variables;
        }

        return _environmentVariableSuggestionService
            .Build(globalVars, env.BuildModel().Variables)
            .Select(s => new EnvVarSuggestion(s.Name, s.Value))
            .ToList();
    }

    private void OnEnvironmentVariablesChanged(object? sender, EventArgs e)
    {
        if (SelectedEnvironment is null || sender is not EnvironmentListItemViewModel changed)
            return;

        if (ReferenceEquals(changed, SelectedEnvironment)
            || (!SelectedEnvironment.IsGlobal && changed.IsGlobal))
            RefreshSelectedEnvironmentSuggestions();

        // Re-run the full dynamic preview whenever the global env's variables change (e.g.
        // the force-override checkbox is toggled). That change alters the effective merge for
        // every concrete env, so the preview values must be recalculated.
        // Also refresh when the selected concrete env's own variables change (e.g. a new
        // response-body variable was added) so the PREVIEW column appears.
        if (changed.IsGlobal || ReferenceEquals(changed, SelectedEnvironment))
        {
            _dynPreviewCts?.Cancel();
            _dynPreviewCts = new CancellationTokenSource();
            _ = RefreshDynamicPreviewsAsync(SelectedEnvironment, _dynPreviewCts.Token);
        }
    }

    /// <summary>
    /// Resolves all dynamic variables for <paramref name="env"/> and stores the results in it
    /// so that variable substitution previews include response-body and mock-data values.
    /// Uses a unified cache namespace: for global env, the preview environment's ID is used,
    /// so cached values are shared with the send pipeline when that env is active.
    /// Falls back silently on failure.
    /// </summary>
    private async Task RefreshDynamicPreviewsAsync(
        EnvironmentListItemViewModel env, CancellationToken ct)
    {
        if (_collectionFolderPath is null) return;

        var model = env.BuildModel();

        var globalItem = Environments.FirstOrDefault(e => e.IsGlobal);
        if (globalItem is null) return; // Collection always has a global env; guard against edge cases.
        var globalModel = globalItem.BuildModel();

        // ── Determine cache namespace, static context, and global preview baseline ──
        string cacheNamespace;
        IReadOnlyDictionary<string, string> staticContext;
        IReadOnlyDictionary<string, string> globalContextForPreview;
        IReadOnlyDictionary<string, MockDataEntry> globalMockGenerators;

        if (env.IsGlobal)
        {
            // Use the selected preview env's ID as the cache namespace so that cache entries
            // written here are found (and reused) by the send pipeline when that env is active.
            var contextEnv = SelectedGlobalPreviewEnvironment ?? Environments.FirstOrDefault(e => !e.IsGlobal);
            var contextEnvModel = contextEnv?.BuildModel();
            cacheNamespace = contextEnvModel is not null
                ? contextEnvModel.EnvironmentId.ToString("N")
                : globalModel.EnvironmentId.ToString("N");

            // For global env, the merged context (global + preview-env statics) is used both as
            // the resolution context and as the baseline for the preview column so that
            // {{base-url}} references from the preview env resolve in global static var previews.
            staticContext = _mergeService.BuildStaticMerge(globalModel, contextEnvModel);
            globalContextForPreview = staticContext;
            globalMockGenerators = new Dictionary<string, MockDataEntry>();
        }
        else
        {
            // Concrete env: use the env's own ID as the cache namespace.
            cacheNamespace = model.EnvironmentId.ToString("N");

            // For resolution, use the full merged context (global + concrete statics).
            staticContext = _mergeService.BuildStaticMerge(globalModel, model);

            // For the preview baseline, expose only global statics so that the concrete env's
            // own statics can still override them in BuildResolvedEnvironment.
            globalContextForPreview = _mergeService.BuildStaticMerge(globalModel, null);

            // Global mock generators so that {{faker-*}} tokens from the global env resolve.
            var mockGens = new Dictionary<string, MockDataEntry>(StringComparer.Ordinal);
            foreach (var v in globalModel.Variables
                .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.MockData
                         && !string.IsNullOrWhiteSpace(v.Name)))
            {
                var entry = v.GetMockEntry();
                if (entry is not null)
                    mockGens[v.Name] = entry;
            }
            globalMockGenerators = mockGens;
        }

        // ── Push global context so {{token}}-style refs resolve in the preview column ──
        env.SetGlobalPreviewValues(globalContextForPreview, globalMockGenerators);

        // ── Push override flags synchronously (no HTTP needed) ────────────────────────
        PushOverrideFlags(env);

        // ── Categorise dynamic variables ──────────────────────────────────────────────
        var mockGenerators = new Dictionary<string, MockDataEntry>(StringComparer.Ordinal);
        var responseBodyVars = new List<EnvironmentVariable>();

        foreach (var v in model.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            if (v.VariableType == EnvironmentVariable.VariableTypes.MockData)
            {
                var entry = v.GetMockEntry();
                if (entry is not null) mockGenerators[v.Name.Trim()] = entry;
            }
            else if (v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody
                     && !string.IsNullOrEmpty(v.ResponseRequestName))
            {
                responseBodyVars.Add(v);
            }
        }

        if (mockGenerators.Count == 0 && responseBodyVars.Count == 0) return;

        // ── Apply mock-data previews immediately (synchronous — no HTTP needed) ──────
        if (mockGenerators.Count > 0)
            env.ApplyMockDataPreviews(mockGenerators);

        // ── Resolve all response-body variables together ────────────────────────────
        // All variables are resolved in a single ResolveAsync call so that the two-pass
        // mechanism can handle vars whose values reference other response-body vars.
        foreach (var variable in responseBodyVars)
        {
            var key = variable.Name.Trim();
            var varVm = env.Variables.FirstOrDefault(v => v.Name.Trim() == key && v.IsResponseBody);
            varVm?.MarkDynamicPreviewLoading();
        }

        _ = ResolveAllResponseBodyVarPreviewsAsync(env, responseBodyVars, staticContext, cacheNamespace, ct);
    }

    /// <summary>
    /// Resolves all response-body variables for the editor preview in a single
    /// <see cref="IDynamicVariableEvaluator.ResolveAsync"/> call so that the two-pass mechanism
    /// can correctly resolve variables whose values reference other response-body variables.
    /// Updates the UI for each variable when complete.
    /// Falls back silently to the error indicator on failure.
    /// </summary>
    private async Task ResolveAllResponseBodyVarPreviewsAsync(
        EnvironmentListItemViewModel env,
        IReadOnlyList<EnvironmentVariable> variables,
        IReadOnlyDictionary<string, string> staticContext,
        string cacheNamespace,
        CancellationToken ct)
    {
        if (_collectionFolderPath is null) return;

        try
        {
            var resolved = await _dynamicEvaluator
                .ResolveAsync(
                    _collectionFolderPath,
                    cacheNamespace,
                    variables,
                    staticContext,
                    allowStaleCache: true,
                    ct)
                .ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();

            foreach (var variable in variables)
            {
                var key = variable.Name.Trim();

                string? resolvedValue = null;
                if (!resolved.FailedVariables.Contains(key) && !resolved.FailedVariables.Contains(variable.Name))
                {
                    resolvedValue = resolved.Variables.TryGetValue(key, out var val)
                                    || resolved.Variables.TryGetValue(variable.Name, out val) ? val : null;
                }

                env.ApplySingleResponseBodyPreview(variable.Name, resolvedValue, failed: resolvedValue is null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Env selection changed — clear the loading indicator without showing an error.
            foreach (var variable in variables)
            {
                var key = variable.Name.Trim();
                var varVm = env.Variables.FirstOrDefault(v => v.Name.Trim() == key && v.IsResponseBody);
                varVm?.ClearDynamicPreviewState();
                varVm?.NotifyPreviewChanged();
            }
        }
        catch (Exception ex)
        {
            // Debug level is intentional: a preview failure (e.g. transient network error
            // while the user is typing) is not a user-visible application error — the UI
            // already shows the "failed" state on the affected variable rows. Logging at
            // Warning would generate noise for expected transient failures during editing.
            _logger.LogDebug(ex, "Dynamic variable preview failed");
            foreach (var variable in variables)
                env.ApplySingleResponseBodyPreview(variable.Name, null, failed: true);
        }
    }

    /// <summary>
    /// Pushes a simple override warning flag to each variable row in <paramref name="env"/>.
    /// <list type="bullet">
    ///   <item>
    ///     Global env: flag is set when a same-named variable exists in the preview environment
    ///     AND the global var does not have force-override set (i.e., it loses to the concrete var).
    ///   </item>
    ///   <item>
    ///     Concrete env: flag is set only when a same-named global variable has
    ///     <see cref="EnvironmentVariableItemViewModel.IsForceGlobalOverride"/> set to
    ///     <see langword="true"/>, meaning the concrete variable will actually be overridden at runtime.
    ///   </item>
    /// </list>
    /// No network calls are needed — only variable names and IsForceGlobalOverride are consulted.
    /// </summary>
    private void PushOverrideFlags(EnvironmentListItemViewModel env)
    {
        if (env.IsGlobal)
        {
            var previewEnv = SelectedGlobalPreviewEnvironment ?? Environments.FirstOrDefault(e => !e.IsGlobal);
            var previewVarNames = previewEnv is not null
                ? previewEnv.BuildModel().Variables
                    .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                    .Select(v => v.Name.Trim())
                    .ToHashSet(StringComparer.Ordinal)
                : (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

            var overriddenSet = env.BuildModel().Variables
                .Where(v => !string.IsNullOrWhiteSpace(v.Name)
                         && !v.IsForceGlobalOverride
                         && previewVarNames.Contains(v.Name.Trim()))
                .Select(v => v.Name.Trim())
                .ToHashSet(StringComparer.Ordinal);

            var previewEnvName = previewEnv?.Name ?? "the preview environment";
            env.SetOverrideFlags(overriddenSet,
                _ => $"Overridden by a same-named variable in {previewEnvName}");
        }
        else
        {
            // Only flag concrete vars that are actually overridden — i.e. a same-named global var
            // has IsForceGlobalOverride = true so the global value wins at runtime.
            var globalItem = Environments.FirstOrDefault(e => e.IsGlobal);
            var overridingGlobalNames = globalItem is not null
                ? globalItem.BuildModel().Variables
                    .Where(v => !string.IsNullOrWhiteSpace(v.Name) && v.IsForceGlobalOverride)
                    .Select(v => v.Name.Trim())
                    .ToHashSet(StringComparer.Ordinal)
                : (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

            env.SetOverrideFlags(overridingGlobalNames,
                _ => "Overridden by a global variable");
        }
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

            var list = await listTask;
            var globalModel = await globalTask;

            Environments.Clear();

            // Global env is always pinned first.
            Environments.Add(CreateListItem(globalModel, isGlobal: true));

            // ListEnvironmentsAsync already returns environments in the saved order
            // (from environment/_meta.json), so no additional sorting needed here.
            foreach (var model in list)
                Environments.Add(CreateListItem(model));

            // Restore the saved global preview environment selection without dirtying anything.
                // Restore the saved global preview environment selection.
                if (globalModel.GlobalPreviewEnvironmentName is { } savedName)
                    SelectedGlobalPreviewEnvironment = Environments
                        .FirstOrDefault(e => !e.IsGlobal
                            && string.Equals(e.Name, savedName, StringComparison.OrdinalIgnoreCase));

            // If the editor was opened while environments were loading, apply the explicit
            // open-time selection now.
            if (_hasPendingOpenEditorSelection)
            {
                ApplyManagerOpenSelection(_pendingOpenEditorEnvironmentFilePath);
                _hasPendingOpenEditorSelection = false;
                _pendingOpenEditorEnvironmentFilePath = null;
            }

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

    private void ApplyManagerOpenSelection(string? activeEnvironmentFilePath)
    {
        // Requirement: opening manager with "(no environment)" selected should open Global.
        if (string.IsNullOrWhiteSpace(activeEnvironmentFilePath))
        {
            SelectedEnvironment = Environments.FirstOrDefault(e => e.IsGlobal)
                ?? (Environments.Count > 0 ? Environments[0] : null);
            return;
        }

        var activeMatch = Environments.FirstOrDefault(e =>
            !e.IsGlobal &&
            string.Equals(e.FilePath, activeEnvironmentFilePath, StringComparison.OrdinalIgnoreCase));

        if (activeMatch is not null)
        {
            SelectedEnvironment = activeMatch;
            return;
        }

        SelectedEnvironment = Environments.FirstOrDefault(e => e.IsGlobal)
            ?? (Environments.Count > 0 ? Environments[0] : null);
    }

    private EnvironmentListItemViewModel CreateListItem(EnvironmentModel model, bool isGlobal = false)
    {
        var item = new EnvironmentListItemViewModel(
            model,
            onRenameCommit: RenameAsync,
            onDeleteRequest: (i, ct) => { BeginDelete(i); return Task.CompletedTask; },
            onCloneRequest: (i, ct) => CloneImmediateAsync(i, ct),
            isGlobal: isGlobal,
            collectionFolderPath: _collectionFolderPath ?? "",
            undoRedoService: _undoRedoService);

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
