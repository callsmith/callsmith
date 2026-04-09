using Avalonia.Controls;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// A <see cref="TreeView"/> subclass whose sole purpose is to vend
/// <see cref="CollectionTreeViewItem"/> containers. The horizontal-scroll
/// suppression is implemented on the item, not the tree, because
/// <c>OnRequestBringIntoView</c> is a virtual method on <see cref="TreeViewItem"/>.
/// </summary>
public sealed class CollectionTreeView : TreeView
{
    // Avalonia 11 uses the runtime type to look up theme styles. Without this override
    // the Fluent theme cannot find a style for CollectionTreeView and applies no template,
    // making the control invisible.
    protected override Type StyleKeyOverride => typeof(TreeView);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        => new CollectionTreeViewItem();
}
