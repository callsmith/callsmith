using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// A <see cref="TreeViewItem"/> subclass that suppresses automatic scrolling
/// on selection/focus changes. Avalonia calls the virtual
/// <see cref="OnRequestBringIntoView"/> on the item itself; overriding it here
/// (rather than handling the bubbled event on the parent <see cref="TreeView"/>)
/// is the correct interception point. Marking the event handled prevents the
/// enclosing <see cref="Avalonia.Controls.ScrollViewer"/> from adjusting its
/// offset, eliminating the horizontal-scroll jitter on click without affecting
/// the explicit scroll-into-view call used by keyboard reveal (Alt+R).
/// </summary>
public sealed class CollectionTreeViewItem : TreeViewItem
{
    // Avalonia 11 uses the runtime type for style/template lookup. Without this
    // the Fluent theme cannot find a style for CollectionTreeViewItem.
    protected override Type StyleKeyOverride => typeof(TreeViewItem);

    protected override void OnRequestBringIntoView(RequestBringIntoViewEventArgs e) =>
        e.Handled = true;
}
