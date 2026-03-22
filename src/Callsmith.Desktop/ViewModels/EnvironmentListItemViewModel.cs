using System.Collections.ObjectModel;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Desktop.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Represents a single environment in the environment editor list.
/// Manages its own variable rows and inline rename state.
/// The parent <see cref="EnvironmentEditorViewModel"/> is notified when the name
/// is committed so it can perform the disk rename.
/// </summary>
public sealed partial class EnvironmentListItemViewModel : ObservableObject
{
    private EnvironmentModel _model;
    private readonly Func<EnvironmentListItemViewModel, string, CancellationToken, Task> _onRenameCommit;
    private readonly Func<EnvironmentListItemViewModel, CancellationToken, Task> _onDeleteRequest;
    private readonly Func<EnvironmentListItemViewModel, CancellationToken, Task>? _onCloneRequest;
    private readonly Action? _onCancelRename;

    // Pre-evaluated dynamic variable values for the PREVIEW box.
    // Populated asynchronously by EnvironmentEditorViewModel after selection.
    private Dictionary<string, string> _resolvedDynVars = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, MockDataEntry> _mockGenerators
        = new Dictionary<string, MockDataEntry>();

    // Pre-resolved global environment vars (global statics + global dynamics resolved for this
    // env's context). Used as baseline in BuildResolvedEnvironment so that tokens like
    // {{base-url}} and {{token}} that live in the global env resolve in the preview column.
    private Dictionary<string, string> _globalPreviewVars = new(StringComparer.Ordinal);

    // Mock-data generators from the global environment so that {{faker-internet-example-email}}
    // and similar global mock vars resolve in the preview column of concrete environments.
    private IReadOnlyDictionary<string, MockDataEntry> _globalMockGenerators
        = new Dictionary<string, MockDataEntry>();
    private IReadOnlyList<EnvVarSuggestion> _suggestions = [];

    // ─── Observable state ────────────────────────────────────────────────────

    /// <summary>Display name. Updated after a successful rename.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>True while the inline rename text box is active.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>Draft name value while renaming (bound two-way to the rename TextBox).</summary>
    [ObservableProperty]
    private string _pendingName = string.Empty;

    /// <summary>Validation message shown while renaming. Empty means no error.</summary>
    [ObservableProperty]
    private string _renameError = string.Empty;

    /// <summary>True when the variable list has been changed but not yet saved.</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>Display color for this environment (hex string, e.g. "#4ec9b0") or null for no color.</summary>
    [ObservableProperty]
    private string? _color;

    /// <summary>
    /// <see langword="true"/> when this is the pinned collection-scoped global environment.
    /// Global environments cannot be renamed, deleted, cloned, or reordered.
    /// </summary>
    public bool IsGlobal { get; }

    // ─── Variable rows ───────────────────────────────────────────────────────

    /// <summary>Editable variable rows for this environment.</summary>
    public ObservableCollection<EnvironmentVariableItemViewModel> Variables { get; } = [];

    public event EventHandler? VariablesChanged;

    /// <summary>
    /// Callback provided by the host that opens the mock-data picker dialog.
    /// Returns the updated variable if confirmed, or null if cancelled.
    /// </summary>
    internal Func<EnvironmentVariable?, Task<EnvironmentVariable?>>? EditMockDataCallback { get; set; }

    /// <summary>
    /// Callback provided by the host that opens the response-body config dialog.
    /// Returns the updated variable if confirmed, or null if cancelled.
    /// </summary>
    internal Func<EnvironmentVariable?, Task<EnvironmentVariable?>>? EditResponseBodyCallback { get; set; }

    // ─── Read-only identity ──────────────────────────────────────────────────

    /// <summary>Absolute path of the backing file on disk.</summary>
    public string FilePath => _model.FilePath;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public EnvironmentListItemViewModel(
        EnvironmentModel model,
        Func<EnvironmentListItemViewModel, string, CancellationToken, Task> onRenameCommit,
        Func<EnvironmentListItemViewModel, CancellationToken, Task> onDeleteRequest,
        Func<EnvironmentListItemViewModel, CancellationToken, Task>? onCloneRequest = null,
        Action? onCancelRename = null,
        bool isGlobal = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(onRenameCommit);
        ArgumentNullException.ThrowIfNull(onDeleteRequest);

        _model = model;
        _name = model.Name;
        _color = model.Color;
        _onRenameCommit = onRenameCommit;
        _onDeleteRequest = onDeleteRequest;
        _onCloneRequest = onCloneRequest;
        _onCancelRename = onCancelRename;
        IsGlobal = isGlobal;

        LoadVariables(model.Variables);
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Starts inline rename mode, pre-filling the text box with the current name.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private void BeginRename()
    {
        PendingName = Name;
        RenameError = string.Empty;
        IsRenaming = true;
    }

    /// <summary>Accepts the pending name and delegates disk rename to the parent ViewModel.</summary>
    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        var trimmed = PendingName.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            RenameError = "Name cannot be empty.";
            return;
        }

        if (string.Equals(trimmed, Name, StringComparison.Ordinal))
        {
            IsRenaming = false;
            return;
        }

        RenameError = string.Empty;
        IsRenaming = false;

        await _onRenameCommit(this, trimmed, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Cancels inline rename without persisting changes.</summary>
    [RelayCommand]
    private void CancelRename()
    {
        PendingName = string.Empty;
        RenameError = string.Empty;
        IsRenaming = false;
        _onCancelRename?.Invoke();
    }

    /// <summary>Requests that the parent ViewModel deletes this environment.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteAsync()
    {
        await _onDeleteRequest(this, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Requests that the parent ViewModel clones this environment.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        if (_onCloneRequest is not null)
            await _onCloneRequest(this, CancellationToken.None).ConfigureAwait(true);
    }

    private bool CanModify => !IsGlobal;
    private bool CanClone => !IsGlobal && _onCloneRequest is not null;

    /// <summary>Adds a new empty variable row.</summary>
    [RelayCommand]
    private void AddVariable()
    {
        Variables.Add(CreateVariableItem(new EnvironmentVariable
        {
            Name = string.Empty,
            Value = string.Empty,
        }));
        IsDirty = true;
    }

    // ─── Internal helpers ────────────────────────────────────────────────────

    partial void OnColorChanged(string? value) => IsDirty = true;

    [RelayCommand]
    private void ClearColor()
    {
        Color = null;
        IsDirty = true;
    }


    /// <summary>
    /// Updates the backing model reference after a successful disk rename.
    /// Called by <see cref="EnvironmentEditorViewModel"/> on rename success.
    /// </summary>
    internal void ApplyRename(EnvironmentModel renamedModel)
    {
        _model = renamedModel;
        Name = renamedModel.Name;
    }

    /// <summary>
    /// Reverts all unsaved changes by reloading variables and color from the last-saved backing model.
    /// </summary>
    internal void Revert()
    {
        Color = _model.Color;            // may transiently set IsDirty = true
        LoadVariables(_model.Variables); // resets IsDirty = false
    }

    /// <summary>
    /// Updates the backing model to reflect the newly saved state and clears the dirty flag.
    /// Called by <see cref="EnvironmentEditorViewModel"/> after a successful save.
    /// </summary>
    internal void MarkSaved(EnvironmentModel savedModel)
    {
        _model = savedModel;
        IsDirty = false;
    }

    /// <summary>
    /// Builds a new <see cref="EnvironmentModel"/> from the current variable rows.
    /// Used to produce the value that will be saved to disk.
    /// </summary>
    internal EnvironmentModel BuildModel()
    {
        var variables = Variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => v.BuildModel())
            .ToList();

        return _model with { Variables = variables, Color = Color };
    }

    internal void SetSuggestions(IReadOnlyList<EnvVarSuggestion> suggestions)
    {
        _suggestions = suggestions;
        foreach (var variable in Variables)
            variable.SuggestionNames = suggestions;
    }

    private void LoadVariables(IReadOnlyList<EnvironmentVariable> variables)
    {
        Variables.Clear();
        foreach (var v in variables)
            Variables.Add(CreateVariableItem(v));
        // Reset dirty flag — initial population of rows from disk is not a user change.
        IsDirty = false;
    }

    private EnvironmentVariableItemViewModel CreateVariableItem(EnvironmentVariable variable)
    {
        // Migrate legacy dynamic (segment-based) var to the new typed model on load.
        var migrated = MigrateLegacyVariable(variable);

        var item = new EnvironmentVariableItemViewModel(
            onDelete: v => { Variables.Remove(v); OnAnyVariableChanged(); },
            onChanged: OnAnyVariableChanged,
            getResolvedEnv: BuildResolvedEnvironment,
            editMockData: v => EditMockDataCallback?.Invoke(v) ?? Task.FromResult<EnvironmentVariable?>(null),
            editResponseBody: v => EditResponseBodyCallback?.Invoke(v) ?? Task.FromResult<EnvironmentVariable?>(null))
        {
            Name = migrated.Name,
            Value = migrated.Value,
            IsSecret = migrated.IsSecret,
            SuggestionNames = _suggestions,
            VariableType = migrated.VariableType,
            MockDataCategory = migrated.MockDataCategory,
            MockDataField = migrated.MockDataField,
            ResponseRequestName = migrated.ResponseRequestName,
            ResponsePath = migrated.ResponsePath,
            ResponseFrequency = migrated.ResponseFrequency,
            ResponseExpiresAfterSeconds = migrated.ResponseExpiresAfterSeconds,
        };
        return item;
    }

    /// <summary>
    /// Migrates a legacy segment-based <see cref="EnvironmentVariable.VariableTypes.Dynamic"/> variable
    /// to the new typed model (mock-data or response-body) when loaded from disk.
    /// Pure static variables and already-typed variables are returned unchanged.
    /// </summary>
    private static EnvironmentVariable MigrateLegacyVariable(EnvironmentVariable variable)
    {
        if (variable.VariableType != EnvironmentVariable.VariableTypes.Dynamic
            || variable.Segments is not { Count: > 0 })
            return variable;

        // Single segment — migrate cleanly
        if (variable.Segments.Count == 1)
        {
            switch (variable.Segments[0])
            {
                case MockDataSegment m:
                    return new EnvironmentVariable
                    {
                        Name = variable.Name,
                        Value = variable.Value,
                        IsSecret = variable.IsSecret,
                        VariableType = EnvironmentVariable.VariableTypes.MockData,
                        MockDataCategory = m.Category,
                        MockDataField = m.Field,
                    };
                case DynamicValueSegment d:
                    return new EnvironmentVariable
                    {
                        Name = variable.Name,
                        Value = variable.Value,
                        IsSecret = variable.IsSecret,
                        VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                        ResponseRequestName = d.RequestName,
                        ResponsePath = d.Path,
                        ResponseFrequency = d.Frequency,
                        ResponseExpiresAfterSeconds = d.ExpiresAfterSeconds,
                    };
            }
        }

        // Composite or unrecognised — downgrade to static with serialised value for now.
        return new EnvironmentVariable
        {
            Name = variable.Name,
            IsSecret = variable.IsSecret,
            VariableType = EnvironmentVariable.VariableTypes.Static,
            Value = Callsmith.Core.Helpers.SegmentSerializer.SerializeSegments(variable.Segments),
        };
    }

    /// <summary>
    /// Builds a <see cref="ResolvedEnvironment"/> for variable substitution previews.
    /// Starts with the resolved global environment vars as a baseline so that tokens like
    /// {{base-url}} and {{token}} from the global env expand in this env's preview column.
    /// Own static vars and dynamic vars are layered on top (own vars override global).
    /// Mock generators are merged from global (baseline) + own (override) so that global
    /// mock vars like {{faker-internet-example-email}} also resolve.
    /// </summary>
    private ResolvedEnvironment BuildResolvedEnvironment()
    {
        // Global vars as the baseline; own vars override.
        var vars = new Dictionary<string, string>(_globalPreviewVars, StringComparer.Ordinal);
        foreach (var v in Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            var key = v.Name.Trim();
            if (v.IsStatic)
                vars[key] = v.Value;
            else if (_resolvedDynVars.TryGetValue(key, out var dyn))
                vars[key] = dyn;
        }

        // Merge mock generators: global generators as baseline, own generators override.
        IReadOnlyDictionary<string, MockDataEntry> mockGenerators = _mockGenerators;
        if (_globalMockGenerators.Count > 0)
        {
            var merged = new Dictionary<string, MockDataEntry>(_globalMockGenerators, StringComparer.Ordinal);
            foreach (var kv in _mockGenerators)
                merged[kv.Key] = kv.Value;
            mockGenerators = merged;
        }

        return new ResolvedEnvironment { Variables = vars, MockGenerators = mockGenerators };
    }

    /// <summary>
    /// Stores the resolved global environment variables and mock generators so that the
    /// preview column can expand tokens like {{base-url}}, {{token}}, and
    /// {{faker-internet-example-email}} that live in the global environment.
    /// Called by <see cref="EnvironmentEditorViewModel"/> whenever the selected env changes.
    /// </summary>
    internal void SetGlobalPreviewValues(
        IReadOnlyDictionary<string, string> globalVars,
        IReadOnlyDictionary<string, MockDataEntry> globalMockGenerators)
    {
        _globalPreviewVars = new Dictionary<string, string>(globalVars, StringComparer.Ordinal);
        _globalMockGenerators = globalMockGenerators;
        foreach (var v in Variables)
            v.NotifyPreviewChanged();
    }

    /// <summary>
    /// Stores pre-evaluated dynamic variable values so that static variable previews
    /// can fully resolve <c>{{token}}</c>-style references to response-body or mock-data vars.
    /// Called by <see cref="EnvironmentEditorViewModel"/> after an async evaluation pass.
    /// </summary>
    internal void SetDynamicPreviewValues(
        IReadOnlyDictionary<string, string> dynVars,
        IReadOnlyDictionary<string, MockDataEntry> generators)
    {
        _resolvedDynVars = new Dictionary<string, string>(dynVars, StringComparer.Ordinal);
        _mockGenerators = generators;
        foreach (var v in Variables)
            v.NotifyPreviewChanged();
    }

    /// <summary>Marks the environment dirty and refreshes all variable previews.</summary>
    private void OnAnyVariableChanged()
    {
        IsDirty = true;
        foreach (var v in Variables)
            v.NotifyPreviewChanged();
        VariablesChanged?.Invoke(this, EventArgs.Empty);
    }
}
