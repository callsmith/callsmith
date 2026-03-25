using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class CollectionsView : UserControl
{
    private CollectionTreeItemViewModel? _draggedNode;
    private CollectionTreeItemViewModel? _dropTargetFolder;
    private Border? _previousDropTargetBorder;   // folder drop-target highlight
    private Border? _previousDropIndicator;        // insertion line indicator
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6.0;
    private int _dropInsertIndex = -1;

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

        if (DataContext is CollectionsViewModel vm)
        {
            vm.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(CollectionsViewModel.RevealFilePath)
                    && !string.IsNullOrEmpty(vm.RevealFilePath))
                {
                    var path = vm.RevealFilePath;
                    vm.RevealFilePath = string.Empty;
                    RevealRequest(path);
                }
                else if (args.PropertyName == nameof(CollectionsViewModel.PendingImportDialog)
                    && vm.PendingImportDialog is not null)
                {
                    var owner = TopLevel.GetTopLevel(this) as Window;
                    if (owner is null) return;
                    var dialog = new ImportCollectionDialog { DataContext = vm.PendingImportDialog };
                    await dialog.ShowDialog(owner);
                    await vm.OnImportDialogClosed();
                }
            };
        }

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
                if (node.Request?.RequestId is not null)
                    menu.Items.Add(MakeMenuItem("View History", () => vm.ViewRequestHistoryCommand.Execute(node)));

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

        // Find the focused TreeViewItem to know which node to act on.
        var node = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext
                   as CollectionTreeItemViewModel;
        if (node is null) return;

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

    // -------------------------------------------------------------------------
    // Single-click on a folder = toggle expand/collapse
    // -------------------------------------------------------------------------

    private void OnTreeTapped(object? sender, RoutedEventArgs e)
    {
        // If the tap originated on the expander toggle button (the chevron), skip.
        if ((e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null) return;

        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (tvi?.DataContext is not CollectionTreeItemViewModel node) return;

        if (node.IsFolder)
        {
            tvi.IsExpanded = !tvi.IsExpanded;
        }
        else if (DataContext is CollectionsViewModel vm)
        {
            // Always open the request, regardless of whether it was previously "selected".
            vm.OpenRequestCommand.Execute(node);
        }
    }

    // -------------------------------------------------------------------------
    // Double-click = begin rename (avoid collision with tree expand/collapse)
    // -------------------------------------------------------------------------

    private void OnTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        var node = tvi?.DataContext as CollectionTreeItemViewModel;
        if (node is null || node.IsRoot) return;

        vm.BeginRenameCommand.Execute(node);
        e.Handled = true;
    }

    // -------------------------------------------------------------------------
    // Drag to reorder
    // -------------------------------------------------------------------------

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedNode = null;
        _dropTargetFolder = null;
        _dropInsertIndex = -1;
        _isDragging = false;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        // FindAncestorOfType walks the VISUAL tree — required because DataTemplate
        // content is hosted inside TreeViewItem's control template; logical Parent
        // traversal does not reliably reach the TreeViewItem container.
        var tvi = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (tvi?.DataContext is CollectionTreeItemViewModel node && !node.IsRoot)
        {
            _draggedNode = node;
            _dragStartPoint = e.GetPosition(CollectionTree);
        }
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedNode is null) return;

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
            CollectionTree.Cursor = new Cursor(StandardCursorType.Hand);
        }

        // Clear previous visual feedback
        ClearDropVisuals();

        // InputHitTest finds the topmost visual at the current coordinates in the
        // CollectionTree visual subtree — works correctly even with pointer capture active.
        var hit = CollectionTree.InputHitTest(currentPos) as Visual;
        var targetTvi = hit?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (targetTvi?.DataContext is not CollectionTreeItemViewModel targetNode)
        {
            if (!_draggedNode.IsFolder && TryShowBottomDropTarget(currentPos))
                return;

            _dropTargetFolder = null;
            _dropInsertIndex = -1;
            return;
        }
        if (targetNode == _draggedNode) return;

        if (!_draggedNode.IsFolder)
        {
            CollectionTreeItemViewModel? candidateFolder = null;
            TreeViewItem? folderTvi = null;

            if (targetNode.IsFolder || targetNode.IsRoot)
            {
                candidateFolder = targetNode;
                folderTvi = targetTvi;
            }
            else if (targetNode.Parent is not null)
            {
                candidateFolder = targetNode.Parent;
                // For parent folder, find its TreeViewItem which is the parent of our current TreeViewItem
                folderTvi = targetTvi.Parent as TreeViewItem;
            }

            if (candidateFolder is not null && candidateFolder != _draggedNode.Parent)
            {
                _dropTargetFolder = candidateFolder;
                _dropInsertIndex = -1; // append at the end of the destination folder
                // Folder highlight — folder is a direct target, show bg tint on its border.
                if (folderTvi is not null && FindItemBorder(folderTvi) is Border folderBorder)
                {
                    folderBorder.Classes.Add("drop-target");
                    _previousDropTargetBorder = folderBorder;
                }
                return;
            }

            // Allow dropping between items at a different nesting level by targeting
            // the parent folder and showing a flat insertion line.
            if (targetNode.Parent is not null && targetNode.Parent != _draggedNode.Parent)
            {
                var localPos = e.GetPosition(targetTvi);
                var midY = targetTvi.Bounds.Height / 2.0;
                _dropTargetFolder = targetNode.Parent;
                var above = localPos.Y <= midY;
                var targetIndexInParent = targetNode.Parent.Children.IndexOf(targetNode);
                _dropInsertIndex = above ? targetIndexInParent : targetIndexInParent + 1;
                ShowDropIndicator(targetTvi, above);
                return;
            }

            _dropTargetFolder = null;
            _dropInsertIndex = -1;
        }

        if (targetNode.Parent != _draggedNode.Parent) return; // restrict to siblings
        if (targetNode.IsRoot) return;

        // Apply flat insertion-line feedback for same-parent reordering.
        var sameLevelLocalPos = e.GetPosition(targetTvi);
        var sameLevelMidY = targetTvi.Bounds.Height / 2.0;
        var sameLevelAbove = sameLevelLocalPos.Y <= sameLevelMidY;
        ShowDropIndicator(targetTvi, sameLevelAbove);

        var parent = _draggedNode.Parent!;
        var targetIndex = parent.Children.IndexOf(targetNode);
        if (targetIndex < 0) return;

        // For same-parent reorder, store the insertion slot (0..Count) and apply
        // the move on pointer release instead of swapping while dragging.
        _dropTargetFolder = parent;
        _dropInsertIndex = sameLevelAbove ? targetIndex : targetIndex + 1;
    }

    private async void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedNode is not null && _dropTargetFolder is not null && DataContext is CollectionsViewModel vm)
        {
            if (!_draggedNode.IsFolder && _draggedNode.Parent != _dropTargetFolder)
            {
                await vm.MoveRequestToFolderAsync(_draggedNode, _dropTargetFolder, _dropInsertIndex);
            }
            else if (_draggedNode.Parent == _dropTargetFolder)
            {
                var parent = _draggedNode.Parent;
                if (parent is not null && _dropInsertIndex >= 0)
                {
                    var currentIndex = parent.Children.IndexOf(_draggedNode);
                    if (currentIndex >= 0)
                    {
                        // Convert insertion slot to final index expected by MoveItemAsync.
                        var targetIndex = _dropInsertIndex > currentIndex
                            ? _dropInsertIndex - 1
                            : _dropInsertIndex;

                        if (targetIndex >= 0 && targetIndex < parent.Children.Count)
                            await vm.MoveItemAsync(_draggedNode, targetIndex);
                    }
                }
            }
        }

        _dropTargetFolder = null;
        _dropInsertIndex = -1;
        EndDrag(e.Pointer);
    }

    private void OnTreePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ClearDropVisuals();
        _draggedNode = null;
        _dropTargetFolder = null;
        _dropInsertIndex = -1;
        _isDragging = false;
        CollectionTree.Cursor = Cursor.Default;
    }

    private void ClearDropVisuals()
    {
        if (_previousDropTargetBorder is not null)
        {
            _previousDropTargetBorder.Classes.Remove("drop-target");
            _previousDropTargetBorder = null;
        }
        if (_previousDropIndicator is not null)
        {
            _previousDropIndicator.IsVisible = false;
            _previousDropIndicator = null;
        }
    }

    private Border? FindItemBorder(Visual? source)
    {
        if (source is null) return null;
        if (source is Border b && b.Name == "ItemBorder") return b;
        foreach (var child in source.GetVisualChildren().OfType<Visual>())
            if (FindItemBorder(child) is Border found) return found;
        return null;
    }

    private Border? FindDropIndicator(Visual? source)
    {
        if (source is null) return null;
        if (source is Border b && b.Name == "DropIndicator") return b;
        foreach (var child in source.GetVisualChildren().OfType<Visual>())
            if (FindDropIndicator(child) is Border found) return found;
        return null;
    }

    private void ShowDropIndicator(TreeViewItem tvi, bool above)
    {
        if (FindDropIndicator(tvi) is not Border indicator) return;
        indicator.VerticalAlignment = above ? Avalonia.Layout.VerticalAlignment.Top
                                            : Avalonia.Layout.VerticalAlignment.Bottom;
        indicator.IsVisible = true;
        _previousDropIndicator = indicator;
    }

    private bool TryShowBottomDropTarget(Point currentPos)
    {
        if (_draggedNode is null || _draggedNode.IsFolder) return false;
        if (DataContext is not CollectionsViewModel vm) return false;
        if (vm.TreeRoots is not [var root]) return false;

        var lastContainer = CollectionTree
            .GetVisualDescendants()
            .OfType<TreeViewItem>()
            .Where(i => i.IsVisible)
            .Select(i =>
            {
                var bottom = i.TranslatePoint(new Point(0, i.Bounds.Height), CollectionTree);
                return new { Item = i, BottomY = bottom?.Y ?? double.MinValue };
            })
            .OrderBy(x => x.BottomY)
            .LastOrDefault();

        if (lastContainer is null || currentPos.Y < lastContainer.BottomY)
            return false;

        var destinationFolder = root;
        if (destinationFolder == _draggedNode.Parent)
            return false;

        var lineTop = Math.Max(0, lastContainer.BottomY);
        TreeBottomDropIndicator.Margin = new Thickness(-4, lineTop, -2, 0);
        _dropTargetFolder = destinationFolder;
        _dropInsertIndex = destinationFolder.Children.Count;
        TreeBottomDropIndicator.IsVisible = true;
        _previousDropIndicator = TreeBottomDropIndicator;
        return true;
    }

    private void EndDrag(IPointer pointer)
    {
        ClearDropVisuals();
        if (_draggedNode is null) return;
        _draggedNode = null;
        _dropTargetFolder = null;
        _dropInsertIndex = -1;
        _isDragging = false;
        CollectionTree.Cursor = Cursor.Default;
        pointer.Capture(null);
    }

    // -------------------------------------------------------------------------
    // Reveal: expand ancestor TreeViewItems so the target request is visible
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts an async chain that expands one <see cref="TreeViewItem"/> level at a time,
    /// yielding to the layout pass between each level so that child containers are
    /// realized before we descend into them.
    /// </summary>
    private void RevealRequest(string filePath)
    {
        if (DataContext is not CollectionsViewModel vm) return;
        if (vm.TreeRoots is not [var root]) return;

        var chain = BuildAncestorChain(root, filePath);
        if (chain is null) return;

        _ = ExpandChainAsync(CollectionTree, chain, 0);
    }

    /// <summary>
    /// Recursively expands the <see cref="TreeViewItem"/> for each node in
    /// <paramref name="chain"/>, waiting for a layout pass after each expansion so
    /// that the next level's containers are realized before we request them.
    /// Scrolls the leaf item into view at the end.
    /// </summary>
    private static async Task ExpandChainAsync(
        ItemsControl parent, List<CollectionTreeItemViewModel> chain, int depth)
    {
        if (depth >= chain.Count) return;

        // Yield so any pending layout (triggered by the previous expansion) completes
        // before we ask for the container at this depth.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        if (parent.ContainerFromItem(chain[depth]) is not TreeViewItem tvi) return;

        if (depth == chain.Count - 1)
        {
            // Leaf node — scroll it into view.
            tvi.BringIntoView();
        }
        else
        {
            // Folder node — expand it and recurse into the next level.
            tvi.IsExpanded = true;
            await ExpandChainAsync(tvi, chain, depth + 1);
        }
    }

    /// <summary>
    /// Returns the list of nodes from the root down to (and including) the target,
    /// or null if the target is not found.
    /// </summary>
    private static List<CollectionTreeItemViewModel>? BuildAncestorChain(
        CollectionTreeItemViewModel root, string filePath)
    {
        var chain = new List<CollectionTreeItemViewModel> { root };
        return FindAndBuild(root, filePath, chain) ? chain : null;
    }

    private static bool FindAndBuild(
        CollectionTreeItemViewModel node, string filePath,
        List<CollectionTreeItemViewModel> chain)
    {
        if (!node.IsFolder &&
            string.Equals(node.Request?.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var child in node.Children)
        {
            chain.Add(child);
            if (FindAndBuild(child, filePath, chain)) return true;
            chain.RemoveAt(chain.Count - 1);
        }
        return false;
    }
}
