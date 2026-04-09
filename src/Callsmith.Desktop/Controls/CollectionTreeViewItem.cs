using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// A <see cref="TreeViewItem"/> subclass that suppresses horizontal auto-scroll
/// on selection/focus changes. Avalonia calls the virtual
/// <see cref="OnRequestBringIntoView"/> on the item itself; overriding it here
/// (rather than handling the bubbled event on the parent <see cref="TreeView"/>)
/// is the correct interception point. Zeroing the <see cref="RequestBringIntoViewEventArgs.TargetRect"/>
/// width removes the horizontal scroll component while leaving vertical scroll intact.
/// </summary>
public sealed class CollectionTreeViewItem : TreeViewItem
{
    // Avalonia 11 uses the runtime type for style/template lookup. Without this
    // the Fluent theme cannot find a style for CollectionTreeViewItem.
    protected override Type StyleKeyOverride => typeof(TreeViewItem);

    protected override void OnRequestBringIntoView(RequestBringIntoViewEventArgs e)
    {
        e.TargetRect = e.TargetRect.WithWidth(0);
        base.OnRequestBringIntoView(e);
    }
}
