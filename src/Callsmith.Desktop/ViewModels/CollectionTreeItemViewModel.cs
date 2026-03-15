using System.Collections.ObjectModel;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Represents a single node in the collections sidebar tree.
/// A node is either a folder (with children) or a request leaf.
/// Supports inline rename and phantom "new node" creation so the tree never
/// needs a full rebuild for individual CRUD operations.
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

    /// <summary>
    /// True while this is a phantom "new" node being named before the file exists on disk.
    /// Set to false once the file is created and the node becomes a real persisted item.
    /// </summary>
    public bool IsNewNode { get; private set; }

    /// <summary>Child nodes (sub-folders + requests). Mutable so CRUD doesn't require tree rebuilds.</summary>
    public ObservableCollection<CollectionTreeItemViewModel> Children { get; }

    /// <summary>
    /// The underlying request. Null for folder nodes and null for phantom request nodes before
    /// the file is saved to disk.
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

    /// <summary>True while the inline rename TextBox is active.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>Text currently in the rename TextBox.</summary>
    [ObservableProperty]
    private string _editingName = string.Empty;

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
        bool isNewNode,
        IEnumerable<CollectionTreeItemViewModel> children)
    {
        Name = name;
        IsFolder = isFolder;
        Request = request;
        FolderPath = folderPath;
        Parent = parent;
        IsRoot = isRoot;
        IsNewNode = isNewNode;
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
    }

    /// <summary>Applies a folder rename, updating path and display name.</summary>
    internal void ApplyFolderRename(string newName, string newFolderPath)
    {
        Name = newName;
        FolderPath = newFolderPath;
        OnPropertyChanged(nameof(Name));
    }

    /// <summary>
    /// Promotes a phantom "new" node to a real persisted node after the file is saved.
    /// </summary>
    internal void PromoteFromPhantom(string realName, CollectionRequest? request = null, string? folderPath = null)
    {
        IsNewNode = false;
        Name = realName;
        Request = request;
        FolderPath = folderPath;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsNewNode));
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
            isNewNode: false,
            children: Enumerable.Empty<CollectionTreeItemViewModel>());

        if (folder.ItemOrder.Count > 0)
        {
            // Respect the explicit order from _order.json, enabling mixed folder/request ordering.
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
            isNewNode: false,
            children: Enumerable.Empty<CollectionTreeItemViewModel>());

    /// <summary>
    /// Creates a phantom "new" node that immediately enters rename mode.
    /// The node is added to the parent's <see cref="Children"/> collection so it
    /// appears in the tree before the file exists on disk.
    /// </summary>
    public static CollectionTreeItemViewModel NewPhantom(
        bool isFolder,
        string defaultName,
        CollectionTreeItemViewModel parent)
    {
        var node = new CollectionTreeItemViewModel(
            name: defaultName,
            isFolder: isFolder,
            request: null,
            folderPath: null,
            parent: parent,
            isRoot: false,
            isNewNode: true,
            children: Enumerable.Empty<CollectionTreeItemViewModel>());

        node.EditingName = defaultName;
        node.IsRenaming = true;
        return node;
    }
}

