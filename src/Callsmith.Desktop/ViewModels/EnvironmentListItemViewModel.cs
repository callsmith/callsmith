using System.Collections.ObjectModel;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    // ─── Variable rows ───────────────────────────────────────────────────────

    /// <summary>Editable variable rows for this environment.</summary>
    public ObservableCollection<EnvironmentVariableItemViewModel> Variables { get; } = [];

    // ─── Read-only identity ──────────────────────────────────────────────────

    /// <summary>Absolute path of the backing file on disk.</summary>
    public string FilePath => _model.FilePath;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public EnvironmentListItemViewModel(
        EnvironmentModel model,
        Func<EnvironmentListItemViewModel, string, CancellationToken, Task> onRenameCommit,
        Func<EnvironmentListItemViewModel, CancellationToken, Task> onDeleteRequest)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(onRenameCommit);
        ArgumentNullException.ThrowIfNull(onDeleteRequest);

        _model = model;
        _name = model.Name;
        _onRenameCommit = onRenameCommit;
        _onDeleteRequest = onDeleteRequest;

        LoadVariables(model.Variables);
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Starts inline rename mode, pre-filling the text box with the current name.</summary>
    [RelayCommand]
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
    }

    /// <summary>Requests that the parent ViewModel deletes this environment.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        await _onDeleteRequest(this, CancellationToken.None).ConfigureAwait(true);
    }

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
    /// Builds a new <see cref="EnvironmentModel"/> from the current variable rows.
    /// Used to produce the value that will be saved to disk.
    /// </summary>
    internal EnvironmentModel BuildModel()
    {
        var variables = Variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => new EnvironmentVariable
            {
                Name = v.Name.Trim(),
                Value = v.Value,
                IsSecret = v.IsSecret,
                VariableType = EnvironmentVariable.VariableTypes.Static,
            })
            .ToList();

        return _model with { Variables = variables };
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
        var item = new EnvironmentVariableItemViewModel(
            onDelete: v => { Variables.Remove(v); IsDirty = true; },
            onChanged: () => { IsDirty = true; })
        {
            Name = variable.Name,
            Value = variable.Value,
            IsSecret = variable.IsSecret,
            IsEnabled = true,
        };
        return item;
    }
}
