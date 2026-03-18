using System.Collections.ObjectModel;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
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
    private readonly IMessenger _messenger;

    /// <summary>Full flat list of every request in the current collection.</summary>
    private IReadOnlyList<CommandPaletteResult> _allResults = [];

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

    public CommandPaletteViewModel(ICollectionService collectionService, IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(messenger);
        _collectionService = collectionService;
        _messenger = messenger;
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
        _allResults = FlattenRequests(treeRoots);
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
        _allResults = [];
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

        var matches = string.IsNullOrEmpty(query)
            ? _allResults
            : _allResults.Where(r => FuzzyMatch(r, query));

        foreach (var m in matches)
            Results.Add(m);

        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Matches by stripping spaces, dashes, and underscores from both the query and the request name,
    /// then checking that the normalised query is a substring of the normalised name
    /// (case-insensitive).  "find by roles" therefore matches "findByRoles".
    /// </summary>
    private static bool FuzzyMatch(CommandPaletteResult result, string query)
    {
        var normQuery = query
          .Replace(" ", string.Empty, StringComparison.Ordinal)
          .Replace("_", string.Empty, StringComparison.Ordinal)
          .Replace("-", string.Empty, StringComparison.Ordinal);
        var normName  = result.Request.Name
          .Replace(" ", string.Empty, StringComparison.Ordinal)
          .Replace("_", string.Empty, StringComparison.Ordinal)
          .Replace("-", string.Empty, StringComparison.Ordinal);
        return normName.Contains(normQuery, StringComparison.OrdinalIgnoreCase);
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
            _messenger.Send(new RequestSelectedMessage(request));
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

    private static IReadOnlyList<CommandPaletteResult> FlattenRequests(
        IReadOnlyList<CollectionTreeItemViewModel> roots)
    {
        var results = new List<CommandPaletteResult>();
        foreach (var root in roots)
            WalkNode(root, string.Empty, results);
        return results;
    }

    private static void WalkNode(
        CollectionTreeItemViewModel node,
        string pathPrefix,
        List<CommandPaletteResult> results)
    {
        if (!node.IsFolder && node.Request is { } request)
        {
            var displayPath = string.IsNullOrEmpty(pathPrefix)
                ? request.Name
                : $"{pathPrefix} / {request.Name}";

            var method = request.Method.Method;
            results.Add(new CommandPaletteResult(
                request,
                displayPath,
                method));
            return;
        }

        // Folder node — recurse into children
        var nextPrefix = node.IsRoot
            ? string.Empty
            : string.IsNullOrEmpty(pathPrefix) ? node.Name : $"{pathPrefix} / {node.Name}";

        foreach (var child in node.Children)
            WalkNode(child, nextPrefix, results);
    }
}
