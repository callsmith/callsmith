using System.Collections.ObjectModel;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Represents a single node in the collections sidebar tree.
/// A node is either a folder (with children) or a request leaf.
/// </summary>
public sealed partial class CollectionTreeItemViewModel : ObservableObject
{
    // -------------------------------------------------------------------------
    // Identity / structure
    // -------------------------------------------------------------------------

    /// <summary>Display name shown in the tree. Updated in-place after rename.</summary>
    public string Name { get; private set; }

    /// <summary>True for folder nodes, false for request nodes.</summary>
    public bool IsFolder { get; }

    /// <summary>True for the root folder of the collection. Root nodes cannot be renamed or deleted.</summary>
    public bool IsRoot { get; }

    /// <summary>Child nodes (sub-folders + requests). Mutable so CRUD doesn't require tree rebuilds.</summary>
    public ObservableCollection<CollectionTreeItemViewModel> Children { get; }

    /// <summary>
    /// The underlying request. Null for folder nodes.
    /// </summary>
    public CollectionRequest? Request { get; private set; }

    /// <summary>Absolute path for folder nodes; null for request nodes.</summary>
    public string? FolderPath { get; private set; }

    /// <summary>Parent node. Null for the root node.</summary>
    public CollectionTreeItemViewModel? Parent { get; }

    // -------------------------------------------------------------------------
    // Observable state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>True when this node's request is the currently active tab.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>The HTTP method string (e.g. "GET") for request nodes; null for folder nodes.</summary>
    public string? MethodName => Request?.Method.Method;

    /// <summary>
    /// Abbreviated method label used in the sidebar pill.
    /// Long verbs are shortened so the pill stays compact; the full name is used everywhere else.
    /// </summary>
    public string? MethodPillLabel => MethodName switch
    {
        "DELETE"  => "DEL",
        "OPTIONS" => "OPT",
        "PATCH"   => "PTCH",
        var m     => m,
    };

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    private CollectionTreeItemViewModel(
        string name,
        bool isFolder,
        CollectionRequest? request,
        string? folderPath,
        CollectionTreeItemViewModel? parent,
        bool isRoot,
        IEnumerable<CollectionTreeItemViewModel> children)
    {
        Name = name;
        IsFolder = isFolder;
        Request = request;
        FolderPath = folderPath;
        Parent = parent;
        IsRoot = isRoot;
        Children = new ObservableCollection<CollectionTreeItemViewModel>(children);
    }

    // -------------------------------------------------------------------------
    // Mutation helpers (called by CollectionsViewModel after disk operations)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies a successful rename to this node, updating the display name and
    /// the underlying request reference (for request nodes).
    /// </summary>
    internal void ApplyRename(string newName, CollectionRequest? updatedRequest = null)
    {
        Name = newName;
        if (updatedRequest is not null)
            Request = updatedRequest;
        OnPropertyChanged(nameof(Name));
    }

    /// <summary>Updates the in-memory request snapshot after a save, without touching the display name.</summary>
    internal void UpdateRequest(CollectionRequest updated)
    {
        Request = updated;
        OnPropertyChanged(nameof(MethodName));
        OnPropertyChanged(nameof(MethodPillLabel));
    }

    /// <summary>Applies a folder rename, updating path and display name.</summary>
    internal void ApplyFolderRename(string newName, string newFolderPath)
    {
        Name = newName;
        FolderPath = newFolderPath;
        OnPropertyChanged(nameof(Name));
    }

    // -------------------------------------------------------------------------
    // Factory methods
    // -------------------------------------------------------------------------

    /// <summary>Creates a folder tree node, recursively building all child nodes.</summary>
    public static CollectionTreeItemViewModel FromFolder(
        CollectionFolder folder,
        CollectionTreeItemViewModel? parent = null,
        bool isRoot = false)
    {
        var node = new CollectionTreeItemViewModel(
            name: folder.Name,
            isFolder: true,
            request: null,
            folderPath: folder.FolderPath,
            parent: parent,
            isRoot: isRoot,
            children: Enumerable.Empty<CollectionTreeItemViewModel>());

        if (folder.ItemOrder.Count > 0)
        {
            // Respect the explicit order from _meta.json, enabling mixed folder/request ordering.
            var requestsByFilename = folder.Requests
                .ToDictionary(r => Path.GetFileName(r.FilePath), StringComparer.OrdinalIgnoreCase);
            var foldersByName = folder.SubFolders
                .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var orderedSet = new HashSet<string>(folder.ItemOrder, StringComparer.OrdinalIgnoreCase);

            foreach (var name in folder.ItemOrder)
            {
                if (requestsByFilename.TryGetValue(name, out var req))
                    node.Children.Add(FromRequest(req, parent: node));
                else if (foldersByName.TryGetValue(name, out var sub))
                    node.Children.Add(FromFolder(sub, parent: node));
            }

            // Append any items absent from the order file (sub-folders first, then requests).
            foreach (var sub in folder.SubFolders.Where(f => !orderedSet.Contains(f.Name)))
                node.Children.Add(FromFolder(sub, parent: node));
            foreach (var req in folder.Requests.Where(r => !orderedSet.Contains(Path.GetFileName(r.FilePath))))
                node.Children.Add(FromRequest(req, parent: node));
        }
        else
        {
            // Default: sub-folders first, then requests (both alphabetical).
            foreach (var sub in folder.SubFolders)
                node.Children.Add(FromFolder(sub, parent: node));
            foreach (var req in folder.Requests)
                node.Children.Add(FromRequest(req, parent: node));
        }

        return node;
    }

    /// <summary>Creates a request leaf node.</summary>
    public static CollectionTreeItemViewModel FromRequest(
        CollectionRequest request,
        CollectionTreeItemViewModel? parent = null) =>
        new(name: request.Name,
            isFolder: false,
            request: request,
            folderPath: null,
            parent: parent,
            isRoot: false,
            children: Enumerable.Empty<CollectionTreeItemViewModel>());

    /// <summary>
    /// Recursively collects all request nodes (leaves) under this folder node.
    /// Returns an empty list if this node is a request node.
    /// </summary>
    internal IEnumerable<CollectionTreeItemViewModel> GetAllRequestsUnder()
    {
        if (!IsFolder)
            yield break;

        foreach (var child in Children)
        {
            if (!child.IsFolder && child.Request is not null)
                yield return child;
            else if (child.IsFolder)
                foreach (var descendant in child.GetAllRequestsUnder())
                    yield return descendant;
        }
    }
}

