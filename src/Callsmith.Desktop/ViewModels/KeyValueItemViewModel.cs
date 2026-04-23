using Callsmith.Desktop.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// A single editable row in a <see cref="KeyValueEditorViewModel"/>.
/// Carries its own delete command so the view needs no parent binding.
/// Stores plain text key/value data (with optional file values in multipart mode).
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
    private string _valueType = ValueTypes.Text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextValue))]
    [NotifyPropertyChangedFor(nameof(IsFileValue))]
    private bool _showValueTypeSelector;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private string _selectedFilePath = string.Empty;

    /// <summary>
    /// Variable suggestions offered by the active environment. Bound to
    /// <c>controls:EnvVarCompletion.Suggestions</c> on key/value TextBoxes.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<EnvVarSuggestion> _suggestionNames = [];

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

    public bool HasSelectedFile => _selectedFileBytes is not null;

    public byte[]? SelectedFileBytes => _selectedFileBytes;

    public string? SelectedFileName => _selectedFileName;

    public KeyValueItemViewModel(Action<KeyValueItemViewModel> onDelete)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        _onDelete = onDelete;
        DeleteCommand = new RelayCommand(() => _onDelete(this));
        SelectFileCommand = new AsyncRelayCommand(SelectFileAsync);
    }

    public void LoadValue(string? value) => Value = value ?? string.Empty;

    public void LoadKey(string? value) => Key = value ?? string.Empty;

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
