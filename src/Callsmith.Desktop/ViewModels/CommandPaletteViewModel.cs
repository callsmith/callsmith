using System.Collections.ObjectModel;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the command palette overlay.
/// Manages fuzzy-search filtering over all requests in the loaded collection tree
/// and opens the selected request when confirmed.
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly ICollectionService _collectionService;
    private readonly ICommandPaletteSearchService _commandPaletteSearchService;
    private readonly IMessenger _messenger;

    /// <summary>Full flat list of every request in the current collection.</summary>
    private IReadOnlyList<CommandPaletteSearchEntry> _allEntries = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private ObservableCollection<CommandPaletteResult> _results = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CommandPaletteResult? _selectedResult;

    [ObservableProperty]
    private bool _isOpen;

    public bool HasResults => Results.Count > 0;

    public CommandPaletteViewModel(
        ICollectionService collectionService,
        IMessenger messenger,
        ICommandPaletteSearchService? commandPaletteSearchService = null)
    {
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(messenger);
        _collectionService = collectionService;
        _messenger = messenger;
        _commandPaletteSearchService = commandPaletteSearchService ?? new CommandPaletteSearchService();
    }

    // -------------------------------------------------------------------------
    // Open / close
    // -------------------------------------------------------------------------

    /// <summary>
    /// Opens the palette pre-loaded with all requests from the given tree.
    /// Clears any previous search.
    /// </summary>
    public void Open(IReadOnlyList<CollectionTreeItemViewModel> treeRoots)
    {
        _allEntries = _commandPaletteSearchService.FlattenRequests(MapTree(treeRoots));
        SearchText = string.Empty;
        ApplyFilter(string.Empty);
        SelectedResult = Results.Count > 0 ? Results[0] : null;
        IsOpen = true;
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        SearchText = string.Empty;
        Results.Clear();
        _allEntries = [];
        SelectedResult = null;
    }

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter(value);
        SelectedResult = Results.Count > 0 ? Results[0] : null;
    }

    private void ApplyFilter(string query)
    {
        Results.Clear();

        var matches = _commandPaletteSearchService.Filter(_allEntries, query);

        foreach (var m in matches)
            Results.Add(new CommandPaletteResult(m.Request, m.DisplayPath, m.MethodName));

        OnPropertyChanged(nameof(HasResults));
    }

    // -------------------------------------------------------------------------
    // Confirm selection
    // -------------------------------------------------------------------------

    [RelayCommand]
    public async Task ConfirmSelectionAsync(CancellationToken ct = default)
    {
        if (SelectedResult is not { } result) return;

        Close();

        try
        {
            // Re-load from disk so secrets are available (same pattern as CollectionsViewModel).
            var request = await _collectionService.LoadRequestAsync(result.Request.FilePath, ct);
            _messenger.Send(new RequestSelectedMessage(request, openAsPermanent: true));
            _messenger.Send(new RevealRequestMessage(result.Request.FilePath));
        }
        catch (OperationCanceledException)
        {
            // User navigated away; safe to ignore.
        }
    }

    // -------------------------------------------------------------------------
    // Keyboard navigation helpers (called from view code-behind)
    // -------------------------------------------------------------------------

    /// <summary>Moves the selection one position up in the results list.</summary>
    public void SelectPrevious()
    {
        if (Results.Count == 0) return;
        var idx = SelectedResult is { } r ? Results.IndexOf(r) : 0;
        SelectedResult = Results[Math.Max(0, idx - 1)];
    }

    /// <summary>Moves the selection one position down in the results list.</summary>
    public void SelectNext()
    {
        if (Results.Count == 0) return;
        var idx = SelectedResult is { } r ? Results.IndexOf(r) : -1;
        SelectedResult = Results[Math.Min(Results.Count - 1, idx + 1)];
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IReadOnlyList<CommandPaletteSearchNode> MapTree(
        IReadOnlyList<CollectionTreeItemViewModel> roots)
    {
        return roots.Select(MapNode).ToList();
    }

    private static CommandPaletteSearchNode MapNode(CollectionTreeItemViewModel node)
    {
        return new CommandPaletteSearchNode
        {
            Name = node.Name,
            IsFolder = node.IsFolder,
            IsRoot = node.IsRoot,
            Request = node.Request,
            Children = node.Children.Select(MapNode).ToList(),
        };
    }
}
