using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Callsmith.Core.Models;
using Callsmith.Desktop.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicSegmentCallback = System.Func<Callsmith.Core.Models.DynamicValueSegment?, System.Threading.Tasks.Task<Callsmith.Core.Models.DynamicValueSegment?>>;
using MockSegmentCallback = System.Func<Callsmith.Core.Models.MockDataSegment?, System.Threading.Tasks.Task<Callsmith.Core.Models.MockDataSegment?>>;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel backing a reusable key/value list editor (headers, query params, env vars).
/// Raises <see cref="Changed"/> whenever any item is added, removed, or edited.
/// </summary>
public sealed partial class KeyValueEditorViewModel : ObservableObject
{
    /// <summary>The editable rows.</summary>
    public ObservableCollection<KeyValueItemViewModel> Items { get; } = [];

    /// <summary>Whether the "+ Add" button is shown.</summary>
    [ObservableProperty]
    private bool _showAddButton = true;

    /// <summary>Whether row-level delete (x) buttons are shown.</summary>
    [ObservableProperty]
    private bool _showDeleteButton = true;
    /// <summary>Whether row enabled/disabled checkboxes are shown.</summary>
    [ObservableProperty]
    private bool _showEnabledToggle = true;

    /// <summary>
    /// When true, the value column supports a Text/File selector for multipart form values.
    /// </summary>
    [ObservableProperty]
    private bool _showValueTypeSelector = false;

    /// <summary>
    /// Callback injected by the parent ViewModel to open the platform file picker.
    /// </summary>
    [ObservableProperty]
    private Func<CancellationToken, Task<(byte[] Bytes, string Name, string Path)?>>? _openFilePickerFunc;
    /// <summary>
    /// Whether the key column renders as a pill-aware field.
    /// Set to true for headers and query params; false (default) for path params and form body.
    /// </summary>
    [ObservableProperty]
    private bool _showKeyPills = false;

    /// <summary>Raised whenever the collection or any item's key/value/enabled state changes.</summary>
    public event EventHandler? Changed;

    // ─── Dialog callbacks for segment editing ────────────────────────────────

    private DynamicSegmentCallback? _editDynamicSegment;
    private MockSegmentCallback? _editMockData;

    /// <summary>
    /// Wires up the dialog callbacks used by each row's <see cref="KeyValueItemViewModel.ValueField"/>
    /// for editing dynamic value segments and mock data segments.
    /// Call this once from the parent ViewModel after the dialogs are available.
    /// </summary>
    public void SetDialogCallbacks(DynamicSegmentCallback editDynamicSegment, MockSegmentCallback editMockData)
    {
        _editDynamicSegment = editDynamicSegment;
        _editMockData = editMockData;

        // Update any already-existing rows (e.g. loaded before callbacks were set).
        foreach (var item in Items)
            item.SetDialogCallbacks(editDynamicSegment, editMockData);
    }

    public KeyValueEditorViewModel()
    {
        Items.CollectionChanged += OnItemsCollectionChanged;
    }

    /// <summary>Adds a new empty row to the editor.</summary>
    [RelayCommand]
    private void AddItem() => Items.Add(CreateItem(string.Empty, string.Empty));

    private void RemoveItem(KeyValueItemViewModel item) => Items.Remove(item);

    /// <summary>
    /// Replaces all current items with those from <paramref name="dict"/>.
    /// Properly manages PropertyChanged subscriptions on each item.
    /// </summary>
    public void LoadFrom(IReadOnlyDictionary<string, string> dict)
    {
        // Unsubscribe before Clear so we don't leak handlers via the Reset event.
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;

        Items.Clear();

        foreach (var (k, v) in dict)
            Items.Add(CreateItem(k, v));
    }

    /// <summary>
    /// Replaces all current items with those from an ordered list of pairs.
    /// Duplicate keys are preserved. Properly manages PropertyChanged subscriptions.
    /// </summary>
    public void LoadFrom(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;

        Items.Clear();

        foreach (var (k, v) in pairs)
            Items.Add(CreateItem(k, v));
    }

    /// <summary>
    /// Replaces all current items with those from an ordered list of <see cref="RequestKv"/> entries.
    /// Enabled state is preserved per item. Duplicate keys are preserved.
    /// </summary>
    public void LoadFrom(IReadOnlyList<RequestKv> items)
    {
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;

        Items.Clear();

        foreach (var kv in items)
            Items.Add(CreateItem(kv.Key, kv.Value, kv.IsEnabled));
    }

    /// <summary>
    /// Moves <paramref name="item"/> to <paramref name="targetIndex"/> in the
    /// <see cref="Items"/> list immediately during drag.
    /// </summary>
    public void MoveItem(KeyValueItemViewModel item, int targetIndex)
    {
        var currentIndex = Items.IndexOf(item);
        if (currentIndex < 0 || currentIndex == targetIndex) return;
        if (targetIndex < 0 || targetIndex >= Items.Count) return;

        Items.Move(currentIndex, targetIndex);
    }

    /// <summary>Returns all enabled rows that have a non-empty key.</summary>
    public IEnumerable<KeyValuePair<string, string>> GetEnabledPairs()
        => Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .Where(i => i.IsTextValue)
            .Where(i => !ShowEnabledToggle || i.IsEnabled)
            .Select(i => new KeyValuePair<string, string>(i.Key, i.Value));

    /// <summary>
    /// Returns all enabled file rows that have a non-empty key and selected file data.
    /// </summary>
    public IReadOnlyList<MultipartFilePart> GetEnabledMultipartFileParts()
        => Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .Where(i => i.IsFileValue && i.SelectedFileBytes is not null)
            .Where(i => !ShowEnabledToggle || i.IsEnabled)
            .Select(i => new MultipartFilePart
            {
                Key = i.Key,
                FileBytes = i.SelectedFileBytes!,
                FileName = i.SelectedFileName,
                FilePath = i.SelectedFilePath,
                IsEnabled = !ShowEnabledToggle || i.IsEnabled,
            })
            .ToList();

    /// <summary>
    /// Returns all multipart rows (enabled and disabled) in their current display order,
    /// as <see cref="MultipartBodyEntry"/> objects that carry type, value, and file metadata.
    /// </summary>
    public IReadOnlyList<MultipartBodyEntry> GetAllMultipartBodyEntries()
        => Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .Select(i => new MultipartBodyEntry
            {
                Key = i.Key,
                IsFile = i.IsFileValue,
                TextValue = i.IsTextValue ? i.Value : null,
                FileName = i.SelectedFileName,
                FilePath = i.SelectedFilePath,
                IsEnabled = !ShowEnabledToggle || i.IsEnabled,
            })
            .ToList();

    /// <summary>
    /// Returns all rows (enabled and disabled) that have a non-empty key as <see cref="RequestKv"/>.
    /// Use this when saving, so disabled items are preserved in the persisted request.
    /// </summary>
    public IReadOnlyList<RequestKv> GetAllKv()
        => Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .Where(i => i.IsTextValue)
            .Select(i => new RequestKv(i.Key, i.Value, !ShowEnabledToggle || i.IsEnabled))
            .ToList();

    /// <summary>
    /// Returns all file rows (enabled and disabled) that have non-empty keys and selected file data.
    /// </summary>
    public IReadOnlyList<MultipartFilePart> GetAllMultipartFileParts()
        => Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .Where(i => i.IsFileValue && i.SelectedFileBytes is not null)
            .Select(i => new MultipartFilePart
            {
                Key = i.Key,
                FileBytes = i.SelectedFileBytes!,
                FileName = i.SelectedFileName,
                FilePath = i.SelectedFilePath,
                IsEnabled = !ShowEnabledToggle || i.IsEnabled,
            })
            .ToList();

    /// <summary>
    /// Replaces all items with a combined multipart list (text + file rows).
    /// </summary>
    public void LoadMultipartFrom(
        IReadOnlyList<KeyValuePair<string, string>> textItems,
        IReadOnlyList<MultipartFilePart> fileItems)
    {
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;

        Items.Clear();

        foreach (var (k, v) in textItems)
            Items.Add(CreateItem(k, v));

        foreach (var file in fileItems)
        {
            Items.Add(CreateItem(
                file.Key,
                string.Empty,
                file.IsEnabled,
                KeyValueItemViewModel.ValueTypes.File,
                file));
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (KeyValueItemViewModel item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;

        if (e.NewItems is not null)
            foreach (KeyValueItemViewModel item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only data-changing properties should raise Changed (and therefore mark a tab dirty).
        // Suggestion and configuration properties (SuggestionNames, ShowDeleteButton, etc.)
        // are display concerns and must not be treated as user edits.
        if (e.PropertyName is
            nameof(KeyValueItemViewModel.Key) or
            nameof(KeyValueItemViewModel.Value) or
            nameof(KeyValueItemViewModel.IsEnabled) or
            nameof(KeyValueItemViewModel.ValueType) or
            nameof(KeyValueItemViewModel.SelectedFilePath))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowDeleteButtonChanged(bool value)
    {
        foreach (var item in Items)
            item.ShowDeleteButton = value;
    }
    partial void OnShowEnabledToggleChanged(bool value)
    {
        // When toggle visibility is disabled, treat all rows as active.
        if (!value)
        {
            foreach (var item in Items)
                item.IsEnabled = true;
        }

        foreach (var item in Items)
            item.ShowEnabledToggle = value;
    }

    partial void OnShowKeyPillsChanged(bool value)
    {
        foreach (var item in Items)
            item.ShowKeyPills = value;
    }

    partial void OnShowValueTypeSelectorChanged(bool value)
    {
        foreach (var item in Items)
            item.ShowValueTypeSelector = value;
    }

    partial void OnOpenFilePickerFuncChanged(Func<CancellationToken, Task<(byte[] Bytes, string Name, string Path)?>>? value)
    {
        foreach (var item in Items)
            item.SetFilePickerCallback(value);
    }

    // ─── Environment variable suggestions ───────────────────────────────────

    private IReadOnlyList<EnvVarSuggestion> _suggestions = [];

    /// <summary>
    /// Updates the variable suggestions shown in every row's value TextBox.
    /// Called by the parent ViewModel when the active environment changes.
    /// </summary>
    public void SetSuggestions(IReadOnlyList<EnvVarSuggestion> suggestions)
    {
        _suggestions = suggestions;
        foreach (var item in Items)
            item.SuggestionNames = suggestions;
    }

    private KeyValueItemViewModel CreateItem(
        string key,
        string value,
        bool isEnabled = true,
        string valueType = KeyValueItemViewModel.ValueTypes.Text,
        MultipartFilePart? filePart = null)
    {
        var item = new KeyValueItemViewModel(RemoveItem, _editDynamicSegment, _editMockData)
        {
            IsEnabled = isEnabled,
            ShowDeleteButton = ShowDeleteButton,
            ShowEnabledToggle = ShowEnabledToggle,
            ShowValueTypeSelector = ShowValueTypeSelector,
            ShowKeyPills = ShowKeyPills,
            SuggestionNames = _suggestions,
            ValueType = valueType,
        };
        item.SetFilePickerCallback(OpenFilePickerFunc);
        item.LoadKey(key);
        item.LoadValue(value);
        if (filePart is not null)
            item.LoadFile(filePart.FileBytes, filePart.FileName, filePart.FilePath);
        return item;
    }
}
