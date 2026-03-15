using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    /// <summary>Raised whenever the collection or any item's key/value/enabled state changes.</summary>
    public event EventHandler? Changed;

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

    /// <summary>Returns all enabled rows that have a non-empty key.</summary>
    public IEnumerable<KeyValuePair<string, string>> GetEnabledPairs()
        => Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .Where(i => !ShowEnabledToggle || i.IsEnabled)
            .Select(i => new KeyValuePair<string, string>(i.Key, i.Value));

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
        => Changed?.Invoke(this, EventArgs.Empty);

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

    private KeyValueItemViewModel CreateItem(string key, string value) =>
        new(RemoveItem)
        {
            Key = key,
            Value = value,
            ShowDeleteButton = ShowDeleteButton,
            ShowEnabledToggle = ShowEnabledToggle,
        };
}
