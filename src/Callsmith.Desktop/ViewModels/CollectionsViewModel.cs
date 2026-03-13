using Avalonia.Platform.Storage;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Callsmith.Desktop.Messages;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the left-hand collections sidebar.
/// Owns the folder tree and handles open-folder and request-selection actions.
/// </summary>
public sealed partial class CollectionsViewModel : ObservableObject
{
    private readonly ICollectionService _collectionService;
    private readonly IMessenger _messenger;

    [ObservableProperty]
    private IReadOnlyList<CollectionTreeItemViewModel> _treeRoots = [];

    [ObservableProperty]
    private CollectionTreeItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _collectionPath = string.Empty;

    [ObservableProperty]
    private bool _hasCollection;

    public CollectionsViewModel(ICollectionService collectionService, IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(messenger);
        _collectionService = collectionService;
        _messenger = messenger;
    }

    /// <summary>
    /// Opens a folder-picker dialog and loads the chosen folder as a collection.
    /// Must be called from the UI thread with a valid <see cref="IStorageProvider"/>.
    /// </summary>
    [RelayCommand]
    public async Task OpenFolderAsync(IStorageProvider storageProvider)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Collection Folder",
            AllowMultiple = false,
        });

        if (folders is not [var folder])
            return;

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        await LoadCollectionAsync(path);
    }

    /// <summary>Reloads the currently open collection from disk.</summary>
    [RelayCommand(CanExecute = nameof(HasCollection))]
    public async Task RefreshAsync()
    {
        if (!string.IsNullOrEmpty(CollectionPath))
            await LoadCollectionAsync(CollectionPath);
    }

    partial void OnSelectedItemChanged(CollectionTreeItemViewModel? value)
    {
        if (value?.Request is CollectionRequest request)
            _messenger.Send(new RequestSelectedMessage(request));
    }

    private async Task LoadCollectionAsync(string path)
    {
        var root = await _collectionService.OpenFolderAsync(path);
        CollectionPath = path;
        HasCollection = true;
        TreeRoots = [CollectionTreeItemViewModel.FromFolder(root)];
        RefreshCommand.NotifyCanExecuteChanged();
        _messenger.Send(new CollectionOpenedMessage(path));
    }
}
