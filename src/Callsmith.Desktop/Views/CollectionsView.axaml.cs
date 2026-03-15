using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class CollectionsView : UserControl
{
    private CollectionTreeItemViewModel? _draggedNode;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6.0;

    public CollectionsView()
    {
        InitializeComponent();
        CollectionTree.AddHandler(InputElement.KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        CollectionTree.AddHandler(InputElement.TappedEvent, OnTreeTapped, RoutingStrategies.Bubble);
        CollectionTree.AddHandler(InputElement.DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble);
        CollectionTree.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);

        // Direct covers events sent to CollectionTree when it holds pointer capture;
        // Bubble covers events that originate from child items during normal movement.
        // handledEventsToo ensures we receive events even if a child has marked them handled.
        var moveRelease = RoutingStrategies.Direct | RoutingStrategies.Bubble;
        CollectionTree.AddHandler(InputElement.PointerMovedEvent, OnTreePointerMoved, moveRelease, handledEventsToo: true);
        CollectionTree.AddHandler(InputElement.PointerReleasedEvent, OnTreePointerReleased, moveRelease, handledEventsToo: true);
        CollectionTree.AddHandler(InputElement.PointerCaptureLostEvent, OnTreePointerCaptureLost, RoutingStrategies.Direct);
    }

    // -------------------------------------------------------------------------
    // Context menu — built in code-behind to avoid AXAML ContextMenu binding hell
    // -------------------------------------------------------------------------

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (CollectionTree.ContextMenu is not ContextMenu menu) return;

        menu.Opening += (_, _) =>
        {
            if (DataContext is not CollectionsViewModel vm) return;
            var node = CollectionTree.SelectedItem as CollectionTreeItemViewModel;

            menu.Items.Clear();

            if (node is null)
            {
                // Nothing selected — surface on the root if we have one
                var root = vm.TreeRoots.Count > 0 ? vm.TreeRoots[0] : null;
                if (root is not null)
                {
                    menu.Items.Add(MakeMenuItem("New Request", () => vm.CreateRequestCommand.Execute(root)));
                    menu.Items.Add(MakeMenuItem("New Folder", () => vm.CreateFolderCommand.Execute(root)));
                }
                return;
            }

            if (node.IsFolder)
            {
                menu.Items.Add(MakeMenuItem("New Request", () => vm.CreateRequestCommand.Execute(node)));
                menu.Items.Add(MakeMenuItem("New Folder", () => vm.CreateFolderCommand.Execute(node)));

                if (!node.IsRoot)
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem("Rename", () => vm.BeginRenameCommand.Execute(node)));
                    menu.Items.Add(MakeMenuItem("Delete Folder", () => vm.DeleteNodeCommand.Execute(node), isDestructive: true));
                }
            }
            else
            {
                menu.Items.Add(MakeMenuItem("Rename", () => vm.BeginRenameCommand.Execute(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem("Delete Request", () => vm.DeleteNodeCommand.Execute(node), isDestructive: true));
            }
        };
    }

    private static MenuItem MakeMenuItem(string header, Action onClick, bool isDestructive = false)
    {
        var item = new MenuItem { Header = header };
        if (isDestructive)
            item.Foreground = Avalonia.Media.Brush.Parse("#f48771");
        item.Click += (_, _) => onClick();
        return item;
    }

    // -------------------------------------------------------------------------
    // Keyboard: F2 = begin rename; Enter/Esc = commit/cancel rename
    // -------------------------------------------------------------------------

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        var node = CollectionTree.SelectedItem as CollectionTreeItemViewModel;
        if (node is null) return;

        if (node.IsRenaming)
        {
            if (e.Key == Key.Enter)
            {
                vm.CommitRenameCommand.Execute(node);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CancelRenameCommand.Execute(node);
                e.Handled = true;
            }
        }
        else
        {
            if (e.Key == Key.F2 && !node.IsRoot)
            {
                vm.BeginRenameCommand.Execute(node);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && !node.IsRoot)
            {
                vm.DeleteNodeCommand.Execute(node);
                e.Handled = true;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Single-click on a folder = toggle expand/collapse
    // -------------------------------------------------------------------------

    private void OnTreeTapped(object? sender, RoutedEventArgs e)
    {
        // If the tap originated on the expander toggle button (the chevron), skip —
        // the TreeViewItem's built-in handler already toggled IsExpanded.
        if ((e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null) return;

        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (tvi?.DataContext is not CollectionTreeItemViewModel node) return;
        if (!node.IsFolder || node.IsRenaming) return;

        tvi.IsExpanded = !tvi.IsExpanded;
    }

    // -------------------------------------------------------------------------
    // Double-click = begin rename (avoid collision with tree expand/collapse)
    // -------------------------------------------------------------------------

    private void OnTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        var node = CollectionTree.SelectedItem as CollectionTreeItemViewModel;
        if (node is null || node.IsRoot || node.IsRenaming) return;

        vm.BeginRenameCommand.Execute(node);
        e.Handled = true;
    }

    // -------------------------------------------------------------------------
    // Drag to reorder
    // -------------------------------------------------------------------------

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedNode = null;
        _isDragging = false;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        // FindAncestorOfType walks the VISUAL tree — required because DataTemplate
        // content is hosted inside TreeViewItem's control template; logical Parent
        // traversal does not reliably reach the TreeViewItem container.
        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (tvi?.DataContext is CollectionTreeItemViewModel node
            && !node.IsRoot && !node.IsNewNode && !node.IsRenaming)
        {
            _draggedNode = node;
            _dragStartPoint = e.GetPosition(CollectionTree);
        }
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedNode is null || DataContext is not CollectionsViewModel vm) return;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        var currentPos = e.GetPosition(CollectionTree);
        if (!_isDragging)
        {
            if (Math.Abs(currentPos.Y - _dragStartPoint.Y) < DragThreshold) return;

            // Capture the pointer so all subsequent events route here as Direct events,
            // regardless of which child element is visually under the cursor.
            e.Pointer.Capture(CollectionTree);
            _isDragging = true;
            CollectionTree.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        }

        // InputHitTest finds the topmost visual at the current coordinates in the
        // CollectionTree visual subtree — works correctly even with pointer capture active.
        var hit = CollectionTree.InputHitTest(currentPos) as Visual;
        var targetTvi = hit?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (targetTvi?.DataContext is not CollectionTreeItemViewModel targetNode) return;
        if (targetNode == _draggedNode) return;
        if (targetNode.Parent != _draggedNode.Parent) return; // restrict to siblings
        if (targetNode.IsRoot) return;

        // Swap only when the cursor crosses the target item's vertical midpoint.
        var localPos = e.GetPosition(targetTvi);
        var midY = targetTvi.Bounds.Height / 2.0;

        var parent = _draggedNode.Parent!;
        var currentIndex = parent.Children.IndexOf(_draggedNode);
        var targetIndex = parent.Children.IndexOf(targetNode);
        if (currentIndex < 0 || targetIndex < 0) return;

        var shouldSwap =
            (targetIndex == currentIndex + 1 && localPos.Y > midY) ||
            (targetIndex == currentIndex - 1 && localPos.Y < midY);

        if (shouldSwap)
            _ = vm.MoveItemAsync(_draggedNode, targetIndex);
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndDrag(e.Pointer);

    private void OnTreePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _draggedNode = null;
        _isDragging = false;
        CollectionTree.Cursor = Cursor.Default;
    }

    private void EndDrag(IPointer pointer)
    {
        if (_draggedNode is null) return;
        _draggedNode = null;
        _isDragging = false;
        CollectionTree.Cursor = Cursor.Default;
        pointer.Capture(null);
    }
}
