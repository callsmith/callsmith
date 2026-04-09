using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// A <see cref="TreeView"/> subclass that suppresses horizontal auto-scroll when
/// Avalonia selects or focuses a tree item. Avalonia raises a
/// <see cref="RequestBringIntoViewEvent"/> on every selection/focus change, which
/// causes the host <see cref="ScrollViewer"/> to jump horizontally when the tree
/// is wider than its viewport. Zeroing <see cref="RequestBringIntoViewEventArgs.TargetRect"/>
/// width removes the horizontal component of the scroll while leaving vertical
/// scroll intact — keyboard navigation and programmatic <c>BringIntoView()</c>
/// calls (e.g. from RevealRequest) continue to scroll vertically as expected.
/// </summary>
public sealed class CollectionTreeView : TreeView
{
    // Avalonia 11 uses the runtime type to look up theme styles. Without this override
    // the Fluent theme cannot find a style for CollectionTreeView and applies no template,
    // making the control invisible.
    protected override Type StyleKeyOverride => typeof(TreeView);

    public CollectionTreeView()
    {
        AddHandler(RequestBringIntoViewEvent, OnRequestBringIntoView, RoutingStrategies.Bubble);
    }

    private static void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        e.TargetRect = e.TargetRect.WithWidth(0);
    }
}
