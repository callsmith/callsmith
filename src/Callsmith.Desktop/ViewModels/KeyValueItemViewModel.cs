using Callsmith.Core.Models;
using Callsmith.Desktop.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// A single editable row in a <see cref="KeyValueEditorViewModel"/>.
/// Carries its own delete command so the view needs no parent binding.
/// The <see cref="ValueField"/> exposes the segmented-value pill control for the value column.
/// </summary>
public sealed partial class KeyValueItemViewModel : ObservableObject
{
    private readonly Action<KeyValueItemViewModel> _onDelete;

    [ObservableProperty]
    private string _key = string.Empty;

    /// <summary>
    /// Raw/plain string value for the row.
    /// Kept in sync with <see cref="ValueField.Text"/> and <see cref="ValueField.GetInlineText"/>.
    /// </summary>
    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _showDeleteButton = true;

    [ObservableProperty]
    private bool _showEnabledToggle = true;

    /// <summary>
    /// When true, the key column renders as a pill-aware field (used for headers and query params).
    /// When false, the key column is a plain TextBox.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKeyPlainInput))]
    [NotifyPropertyChangedFor(nameof(ShowKeyPillView))]
    private bool _showKeyPills = false;

    /// <summary>
    /// Variable suggestions offered by the active environment. Bound to
    /// <c>controls:EnvVarCompletion.Suggestions</c> on the value TextBox.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<EnvVarSuggestion> _suggestionNames = [];

    /// <summary>
    /// Segmented value field for this row's value.
    /// Renders as pills when the value contains <c>{% %}</c> dynamic tokens,
    /// or as a plain TextBox otherwise.
    /// The callbacks are wired up by the parent <see cref="KeyValueEditorViewModel"/>
    /// when segment-editing dialogs are available.
    /// </summary>
    public SegmentedValueFieldViewModel ValueField { get; }

    /// <summary>
    /// Segmented key field for this row.
    /// Only rendered as pills when <see cref="ShowKeyPills"/> is true (headers and query params).
    /// </summary>
    public SegmentedValueFieldViewModel KeyField { get; }

    /// <summary>True when the key pill view is enabled and currently in plain-text mode (no segments).</summary>
    public bool ShowKeyPlainInput => ShowKeyPills && KeyField.ShowPlainInput;

    /// <summary>True when the key pill view is enabled and has at least one dynamic segment.</summary>
    public bool ShowKeyPillView => ShowKeyPills && KeyField.HasSegments;

    /// <summary>Removes this row from its parent editor when executed.</summary>
    public IRelayCommand DeleteCommand { get; }

    public KeyValueItemViewModel(
        Action<KeyValueItemViewModel> onDelete,
        Func<DynamicValueSegment?, Task<DynamicValueSegment?>>? editDynamicSegment = null,
        Func<MockDataSegment?, Task<MockDataSegment?>>? editMockData = null)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        _onDelete = onDelete;
        DeleteCommand = new RelayCommand(() => _onDelete(this));

        SegmentedValueFieldViewModel? field = null;
        field = new SegmentedValueFieldViewModel(
            onChanged: () =>
            {
                // Keep the plain Value property in sync with segment content
                Value = field!.GetInlineText();
            },
            editDynamicSegment: editDynamicSegment,
            editMockData: editMockData);
        ValueField = field;

        SegmentedValueFieldViewModel? keyField = null;
        keyField = new SegmentedValueFieldViewModel(
            onChanged: () =>
            {
                Key = keyField!.GetInlineText();
                OnPropertyChanged(nameof(ShowKeyPlainInput));
                OnPropertyChanged(nameof(ShowKeyPillView));
            },
            editDynamicSegment: editDynamicSegment,
            editMockData: editMockData);
        KeyField = keyField;
    }

    /// <summary>
    /// Loads a value string into this row.
    /// Parses any <c>{% %}</c> tokens into pill segments.
    /// </summary>
    public void LoadValue(string? value)
    {
        Value = value ?? string.Empty;
        ValueField.LoadFromText(Value);
    }

    /// <summary>
    /// Loads a key string into this row.
    /// Parses any <c>{% %}</c> tokens into pill segments when <see cref="ShowKeyPills"/> is enabled.
    /// </summary>
    public void LoadKey(string? value)
    {
        Key = value ?? string.Empty;
        KeyField.LoadFromText(Key);
    }

    /// <summary>
    /// Updates segment-editing dialog callbacks on the value field.
    /// Called by the parent <see cref="KeyValueEditorViewModel"/> when callbacks
    /// are registered after items already exist.
    /// </summary>
    public void SetDialogCallbacks(
        Func<DynamicValueSegment?, Task<DynamicValueSegment?>> editDynamicSegment,
        Func<MockDataSegment?, Task<MockDataSegment?>> editMockData)
    {
        ValueField.SetCallbacks(editDynamicSegment, editMockData);
        KeyField.SetCallbacks(editDynamicSegment, editMockData);
    }
}
