using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Callsmith.Desktop.Views;

public partial class KeyValueEditorView : UserControl
{
    private const double MinSplitterFraction = 0.05;
    private const double MaxSplitterFraction = 0.95;
    private KeyValueItemViewModel? _draggedItem;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6.0;
    private KeyValueEditorViewModel? _trackedVm;
    private bool _applyFractionQueued;

    public KeyValueEditorView()
    {
        InitializeComponent();

        var moveRelease = RoutingStrategies.Direct | RoutingStrategies.Bubble;
        ItemRows.AddHandler(InputElement.PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel);
        ItemRows.AddHandler(InputElement.PointerMovedEvent, OnRowPointerMoved, moveRelease, handledEventsToo: true);
        ItemRows.AddHandler(InputElement.PointerReleasedEvent, OnRowPointerReleased, moveRelease, handledEventsToo: true);
        ItemRows.AddHandler(InputElement.PointerCaptureLostEvent, OnRowPointerCaptureLost, RoutingStrategies.Direct);
        HeaderSplitter.AddHandler(PointerReleasedEvent, OnHeaderSplitterPointerReleased, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _trackedVm.Items.CollectionChanged -= OnItemsCollectionChanged;
        }

        _trackedVm = DataContext as KeyValueEditorViewModel;
        if (_trackedVm is null) return;

        _trackedVm.PropertyChanged += OnViewModelPropertyChanged;
        _trackedVm.Items.CollectionChanged += OnItemsCollectionChanged;
        QueueApplyForVm(_trackedVm);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trackedVm is null) return;
        if (e.PropertyName != nameof(KeyValueEditorViewModel.KeyValueSplitterFraction)) return;
        QueueApplyForVm(_trackedVm);
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_trackedVm is null) return;
        QueueApplyForVm(_trackedVm);
    }

    private void QueueApplyForVm(KeyValueEditorViewModel vm)
    {
        if (_applyFractionQueued) return;
        _applyFractionQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _applyFractionQueued = false;
            if (!ReferenceEquals(_trackedVm, vm)) return;
            ApplyKeyValueSplitterFraction(vm.KeyValueSplitterFraction);
        });
    }

    private void ApplyKeyValueSplitterFraction(double? fraction)
    {
        if (!fraction.HasValue) return;
        if (HeaderGrid.ColumnDefinitions.Count < 5) return;

        var f = Math.Clamp(fraction.Value, MinSplitterFraction, MaxSplitterFraction);
        HeaderGrid.ColumnDefinitions[2].Width = new GridLength(f, GridUnitType.Star);
        HeaderGrid.ColumnDefinitions[4].Width = new GridLength(1 - f, GridUnitType.Star);

        foreach (var grid in ItemRows.GetVisualDescendants().OfType<Grid>())
        {
            if (grid.ColumnDefinitions.Count < 5) continue;
            grid.ColumnDefinitions[2].Width = new GridLength(f, GridUnitType.Star);
            grid.ColumnDefinitions[4].Width = new GridLength(1 - f, GridUnitType.Star);
        }
    }

    private void OnHeaderSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_trackedVm is null) return;
        if (HeaderGrid.ColumnDefinitions.Count < 5) return;

        var keyWidth = HeaderGrid.ColumnDefinitions[2].ActualWidth;
        var valueWidth = HeaderGrid.ColumnDefinitions[4].ActualWidth;
        var total = keyWidth + valueWidth;
        if (total <= 0) return;

        var fraction = Math.Clamp(keyWidth / total, MinSplitterFraction, MaxSplitterFraction);
        _trackedVm.KeyValueSplitterFraction = fraction;
        _trackedVm.SplitterChangedCallback?.Invoke(fraction);
    }

    // ─── Drag to reorder ──────────────────────────────────────────────────────

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedItem = null;
        _isDragging = false;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        if (TryGetRowItem(e.Source as Visual, out var item))
        {
            _draggedItem = item;
            _dragStartPoint = e.GetPosition(ItemRows);
        }
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedItem is null || DataContext is not KeyValueEditorViewModel vm) return;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            EndRowDrag(e.Pointer);
            return;
        }

        var currentPos = e.GetPosition(ItemRows);
        if (!_isDragging)
        {
            if (Math.Abs(currentPos.Y - _dragStartPoint.Y) < DragThreshold) return;

            e.Pointer.Capture(ItemRows);
            _isDragging = true;
            ItemRows.Cursor = new Cursor(StandardCursorType.Hand);
        }

        var hit = ItemRows.InputHitTest(currentPos) as Visual;
        if (!TryGetRowItem(hit, out var targetItem)) return;
        if (targetItem == _draggedItem) return;

        var items = vm.Items;
        var currentIndex = items.IndexOf(_draggedItem);
        var targetIndex = items.IndexOf(targetItem);
        if (currentIndex < 0 || targetIndex < 0) return;

        // Find the row's visual so we can determine drop position.
        if (!TryGetRowVisual(hit, out var targetVisual)) return;

        var destinationIndex = ComputeDestinationIndex(currentIndex, targetIndex, e.GetPosition(targetVisual).Y, targetVisual.Bounds.Height);
        if (destinationIndex == currentIndex) return;

        vm.MoveItem(_draggedItem, destinationIndex);
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndRowDrag(e.Pointer);

    private void OnRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _draggedItem = null;
        _isDragging = false;
        ItemRows.Cursor = Cursor.Default;
    }

    private void EndRowDrag(IPointer pointer)
    {
        if (_draggedItem is null) return;
        _draggedItem = null;
        _isDragging = false;
        ItemRows.Cursor = Cursor.Default;
        pointer.Capture(null);
    }

    private static bool TryGetRowItem(Visual? source, out KeyValueItemViewModel item)
    {
        var current = source;
        while (current is not null)
        {
            if (current is StyledElement element && element.DataContext is KeyValueItemViewModel vm)
            {
                item = vm;
                return true;
            }
            current = current.GetVisualParent();
        }
        item = null!;
        return false;
    }

    private static bool TryGetRowVisual(Visual? source, out Visual visual)
    {
        // Walk up to find the ContentPresenter that owns the row's DataTemplate root.
        var current = source;
        while (current is not null)
        {
            if (current is StyledElement element && element.DataContext is KeyValueItemViewModel)
            {
                visual = current;
                return true;
            }
            current = current.GetVisualParent();
        }
        visual = null!;
        return false;
    }

    private static int ComputeDestinationIndex(int currentIndex, int targetIndex, double localY, double height)
    {
        var insertAfterTarget = localY >= height / 2.0;

        if (currentIndex < targetIndex)
            return insertAfterTarget ? targetIndex : targetIndex - 1;

        return insertAfterTarget ? targetIndex + 1 : targetIndex;
    }
}
