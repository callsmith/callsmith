using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class RequestEditorView : UserControl
{
    private RequestTabViewModel? _draggedTab;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6.0;

    public RequestEditorView()
    {
        InitializeComponent();

        TabStrip.AddHandler(InputElement.PointerPressedEvent, OnTabStripPointerPressed, handledEventsToo: false);
        TabStrip.AddHandler(InputElement.PointerMovedEvent, OnTabStripPointerMoved, handledEventsToo: true);
        TabStrip.AddHandler(InputElement.PointerReleasedEvent, OnTabStripPointerReleased, handledEventsToo: true);
        TabStrip.AddHandler(InputElement.PointerCaptureLostEvent, OnTabStripPointerCaptureLost, handledEventsToo: true);
    }

    private void OnTabStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not RequestEditorViewModel vm) return;

        // Walk up from the pressed element to find the DataContext = RequestTabViewModel.
        var source = e.Source as Control;
        while (source is not null)
        {
            if (source is Border { Classes: { } cls } border && cls.Contains("tab-chip"))
            {
                if (border.DataContext is RequestTabViewModel tab)
                {
                    vm.SelectTabCommand.Execute(tab);
                    _draggedTab = tab;
                    _dragStartPoint = e.GetPosition(TabStrip);
                    _isDragging = false;
                    e.Pointer.Capture(TabStrip);
                    e.Handled = true;
                }
                return;
            }
            source = source.Parent as Control;
        }
    }

    private void OnTabStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedTab is null || DataContext is not RequestEditorViewModel vm) return;

        // Abort if the left button was released outside of our handlers.
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        var currentPos = e.GetPosition(TabStrip);

        if (!_isDragging)
        {
            if (Math.Abs(currentPos.X - _dragStartPoint.X) < DragThreshold) return;
            _isDragging = true;
            TabStrip.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }

        var itemsPanel = TabStrip.ItemsPanelRoot as Panel;
        if (itemsPanel is null) return;

        var panelPoint = e.GetPosition(itemsPanel);
        var targetIndex = GetTargetIndex(panelPoint, vm, itemsPanel);
        if (targetIndex < 0) return;

        var currentIndex = vm.Tabs.IndexOf(_draggedTab);
        if (currentIndex >= 0)
            vm.MoveTab(currentIndex, targetIndex);
    }

    private void OnTabStripPointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndDrag(e.Pointer);

    private void OnTabStripPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _draggedTab = null;
        _isDragging = false;
        TabStrip.Cursor = Cursor.Default;
    }

    private void EndDrag(IPointer pointer)
    {
        if (_draggedTab is null) return;
        _draggedTab = null;
        _isDragging = false;
        TabStrip.Cursor = Cursor.Default;
        pointer.Capture(null);
    }

    /// <summary>
    /// Returns the index to swap the dragged tab with, or -1 if no swap is warranted.
    /// Swaps only with an immediate neighbor once the cursor crosses that neighbor's midpoint,
    /// which prevents the flip-flop instability of simple hit-testing.
    /// </summary>
    private int GetTargetIndex(Point panelPoint, RequestEditorViewModel vm, Panel itemsPanel)
    {
        var currentIndex = vm.Tabs.IndexOf(_draggedTab!);
        if (currentIndex < 0) return -1;

        // Swap with the right neighbor when cursor passes its midpoint.
        if (currentIndex + 1 < itemsPanel.Children.Count)
        {
            var rightBounds = itemsPanel.Children[currentIndex + 1].Bounds;
            if (panelPoint.X > rightBounds.Left + rightBounds.Width / 2)
                return currentIndex + 1;
        }

        // Swap with the left neighbor when cursor passes its midpoint.
        if (currentIndex - 1 >= 0)
        {
            var leftBounds = itemsPanel.Children[currentIndex - 1].Bounds;
            if (panelPoint.X < leftBounds.Left + leftBounds.Width / 2)
                return currentIndex - 1;
        }

        return -1;
    }
}
