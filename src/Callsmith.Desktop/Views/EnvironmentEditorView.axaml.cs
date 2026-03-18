using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class EnvironmentEditorView : UserControl
{
    private EnvironmentListItemViewModel? _draggedItem;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6.0;

    private EnvironmentEditorViewModel? _trackedVm;

    public EnvironmentEditorView()
    {
        InitializeComponent();

        EnvironmentList.AddHandler(InputElement.PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);

        var moveRelease = RoutingStrategies.Direct | RoutingStrategies.Bubble;
        EnvironmentList.AddHandler(InputElement.PointerMovedEvent, OnListPointerMoved, moveRelease, handledEventsToo: true);
        EnvironmentList.AddHandler(InputElement.PointerReleasedEvent, OnListPointerReleased, moveRelease, handledEventsToo: true);
        EnvironmentList.AddHandler(InputElement.PointerCaptureLostEvent, OnListPointerCaptureLost, RoutingStrategies.Direct);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_trackedVm is not null)
            _trackedVm.PropertyChanged -= OnViewModelPropertyChanged;

        _trackedVm = DataContext as EnvironmentEditorViewModel;

        if (_trackedVm is not null)
            _trackedVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvironmentEditorViewModel.ShowDynamicValueConfig))
        {
            if (_trackedVm is null || !_trackedVm.ShowDynamicValueConfig)
                return;
            if (_trackedVm.PendingDynamicConfig is null)
                return;

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return;

            var dialog = new DynamicValueConfigDialog
            {
                DataContext = _trackedVm.PendingDynamicConfig,
            };

            await dialog.ShowDialog(owner);
            _trackedVm.OnDynamicConfigDialogClosed();
        }
        else if (e.PropertyName == nameof(EnvironmentEditorViewModel.ShowMockDataConfig))
        {
            if (_trackedVm is null || !_trackedVm.ShowMockDataConfig)
                return;
            if (_trackedVm.PendingMockDataConfig is null)
                return;

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return;

            var dialog = new MockDataConfigDialog
            {
                DataContext = _trackedVm.PendingMockDataConfig,
            };

            await dialog.ShowDialog(owner);
            _trackedVm.OnMockDataConfigDialogClosed();
        }
    }

    // ─── Drag to reorder ──────────────────────────────────────────────────────

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedItem = null;
        _isDragging = false;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        var lbi = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (lbi?.DataContext is EnvironmentListItemViewModel item && !item.IsRenaming)
        {
            _draggedItem = item;
            _dragStartPoint = e.GetPosition(EnvironmentList);
        }
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedItem is null || DataContext is not EnvironmentEditorViewModel vm) return;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        var currentPos = e.GetPosition(EnvironmentList);
        if (!_isDragging)
        {
            if (Math.Abs(currentPos.Y - _dragStartPoint.Y) < DragThreshold) return;

            // Capture the pointer so all subsequent events route here as Direct events,
            // regardless of which child element is visually under the cursor.
            e.Pointer.Capture(EnvironmentList);
            _isDragging = true;
            EnvironmentList.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        }

        var hit = EnvironmentList.InputHitTest(currentPos) as Visual;
        var targetLbi = hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (targetLbi?.DataContext is not EnvironmentListItemViewModel targetItem) return;
        if (targetItem == _draggedItem || targetItem.IsRenaming) return;

        var localPos = e.GetPosition(targetLbi);
        var midY = targetLbi.Bounds.Height / 2.0;

        var items = vm.Environments;
        var currentIndex = items.IndexOf(_draggedItem);
        var targetIndex = items.IndexOf(targetItem);
        if (currentIndex < 0 || targetIndex < 0) return;

        // Swap only when the cursor crosses the target item's vertical midpoint,
        // matching the direction of movement to avoid jitter.
        var shouldSwap =
            (targetIndex == currentIndex + 1 && localPos.Y > midY) ||
            (targetIndex == currentIndex - 1 && localPos.Y < midY);

        if (shouldSwap)
            _ = vm.MoveEnvironmentAsync(_draggedItem, targetIndex);
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndDrag(e.Pointer);

    private void OnListPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _draggedItem = null;
        _isDragging = false;
        EnvironmentList.Cursor = Cursor.Default;
    }

    private void EndDrag(IPointer pointer)
    {
        if (_draggedItem is null) return;
        _draggedItem = null;
        _isDragging = false;
        EnvironmentList.Cursor = Cursor.Default;
        pointer.Capture(null);
    }

}
