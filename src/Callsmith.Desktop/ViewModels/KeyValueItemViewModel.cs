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
    private Func<CancellationToken, Task<(byte[] Bytes, string Name, string Path)?>>? _openFilePickerFunc;
    private byte[]? _selectedFileBytes;
    private string? _selectedFileName;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextValue))]
    [NotifyPropertyChangedFor(nameof(IsFileValue))]
    [NotifyPropertyChangedFor(nameof(ShowTextValuePlainInput))]
    [NotifyPropertyChangedFor(nameof(ShowTextValuePillView))]
    private string _valueType = ValueTypes.Text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextValue))]
    [NotifyPropertyChangedFor(nameof(IsFileValue))]
    [NotifyPropertyChangedFor(nameof(ShowTextValuePlainInput))]
    [NotifyPropertyChangedFor(nameof(ShowTextValuePillView))]
    private bool _showValueTypeSelector;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private string _selectedFilePath = string.Empty;

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

    /// <summary>Opens the file picker and assigns a file to this row.</summary>
    public IAsyncRelayCommand SelectFileCommand { get; }

    public static class ValueTypes
    {
        public const string Text = "Text";
        public const string File = "File";
    }

    public IReadOnlyList<string> AvailableValueTypes { get; } = [ValueTypes.Text, ValueTypes.File];

    public bool IsTextValue => !ShowValueTypeSelector || ValueType == ValueTypes.Text;

    public bool IsFileValue => ShowValueTypeSelector && ValueType == ValueTypes.File;

    public bool ShowTextValuePlainInput => IsTextValue && ValueField.ShowPlainInput;

    public bool ShowTextValuePillView => IsTextValue && ValueField.HasSegments;

    public bool HasSelectedFile => _selectedFileBytes is not null;

    public byte[]? SelectedFileBytes => _selectedFileBytes;

    public string? SelectedFileName => _selectedFileName;

    public KeyValueItemViewModel(
        Action<KeyValueItemViewModel> onDelete,
        Func<DynamicValueSegment?, Task<DynamicValueSegment?>>? editDynamicSegment = null,
        Func<MockDataSegment?, Task<MockDataSegment?>>? editMockData = null)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        _onDelete = onDelete;
        DeleteCommand = new RelayCommand(() => _onDelete(this));
        SelectFileCommand = new AsyncRelayCommand(SelectFileAsync);

        SegmentedValueFieldViewModel? field = null;
        field = new SegmentedValueFieldViewModel(
            onChanged: () =>
            {
                // Keep the plain Value property in sync with segment content
                Value = field!.GetInlineText();
                OnPropertyChanged(nameof(ShowTextValuePlainInput));
                OnPropertyChanged(nameof(ShowTextValuePillView));
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

    public void SetFilePickerCallback(Func<CancellationToken, Task<(byte[] Bytes, string Name, string Path)?>>? callback)
    {
        _openFilePickerFunc = callback;
    }

    public void LoadFile(byte[] bytes, string? fileName, string? filePath)
    {
        _selectedFileBytes = bytes;
        _selectedFileName = fileName;
        SelectedFilePath = filePath ?? fileName ?? string.Empty;
    }

    private async Task SelectFileAsync(CancellationToken ct)
    {
        if (_openFilePickerFunc is null) return;
        var result = await _openFilePickerFunc(ct);
        if (result is null) return;
        _selectedFileBytes = result.Value.Bytes;
        _selectedFileName = result.Value.Name;
        SelectedFilePath = result.Value.Path;
        OnPropertyChanged(nameof(HasSelectedFile));
    }

    partial void OnValueTypeChanged(string value)
    {
        if (value == ValueTypes.File) return;

        _selectedFileBytes = null;
        _selectedFileName = null;
        SelectedFilePath = string.Empty;
        OnPropertyChanged(nameof(HasSelectedFile));
    }
}
