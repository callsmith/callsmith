using CommunityToolkit.Mvvm.ComponentModel;
using Callsmith.Core.Models;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Represents a single node in the collections sidebar tree.
/// A node is either a <see cref="CollectionFolder"/> or a <see cref="CollectionRequest"/>.
/// </summary>
public sealed partial class CollectionTreeItemViewModel : ObservableObject
{
    /// <summary>Display label shown in the tree.</summary>
    public string Name { get; }

    /// <summary>True when this node represents a folder; false for a request.</summary>
    public bool IsFolder { get; }

    /// <summary>Child nodes (sub-folders and requests within this folder).</summary>
    public IReadOnlyList<CollectionTreeItemViewModel> Children { get; }

    /// <summary>
    /// The underlying request model. Null when <see cref="IsFolder"/> is true.
    /// </summary>
    public CollectionRequest? Request { get; }

    [ObservableProperty]
    private bool _isExpanded = true;

    private CollectionTreeItemViewModel(
        string name,
        bool isFolder,
        CollectionRequest? request,
        IReadOnlyList<CollectionTreeItemViewModel> children)
    {
        Name = name;
        IsFolder = isFolder;
        Request = request;
        Children = children;
    }

    /// <summary>Creates a folder node from a <see cref="CollectionFolder"/>.</summary>
    public static CollectionTreeItemViewModel FromFolder(CollectionFolder folder)
    {
        var children = new List<CollectionTreeItemViewModel>();

        foreach (var sub in folder.SubFolders)
            children.Add(FromFolder(sub));

        foreach (var req in folder.Requests)
            children.Add(FromRequest(req));

        return new CollectionTreeItemViewModel(
            name: folder.Name,
            isFolder: true,
            request: null,
            children: children);
    }

    /// <summary>Creates a leaf node from a <see cref="CollectionRequest"/>.</summary>
    public static CollectionTreeItemViewModel FromRequest(CollectionRequest request) =>
        new(name: request.Name,
            isFolder: false,
            request: request,
            children: []);
}
