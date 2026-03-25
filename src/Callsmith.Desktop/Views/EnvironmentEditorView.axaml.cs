using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class EnvironmentEditorView : UserControl
{
    private EnvironmentListItemViewModel? _draggedItem;
    private EnvironmentVariableItemViewModel? _draggedVariable;
    private Point _dragStartPoint;
    private Point _variableDragStartPoint;
    private bool _isDragging;
    private bool _isVariableDragging;
    private bool _environmentOrderChanged;
    private const double DragThreshold = 6.0;

    private EnvironmentEditorViewModel? _trackedVm;
    private EnvironmentListItemViewModel? _contextMenuEnvItem;

    public EnvironmentEditorView()
    {
        InitializeComponent();

        EnvironmentList.AddHandler(InputElement.PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);

        var moveRelease = RoutingStrategies.Direct | RoutingStrategies.Bubble;
        EnvironmentList.AddHandler(InputElement.PointerMovedEvent, OnListPointerMoved, moveRelease, handledEventsToo: true);
        EnvironmentList.AddHandler(InputElement.PointerReleasedEvent, OnListPointerReleased, moveRelease, handledEventsToo: true);
        EnvironmentList.AddHandler(InputElement.PointerCaptureLostEvent, OnListPointerCaptureLost, RoutingStrategies.Direct);

        VariableRows.AddHandler(InputElement.PointerPressedEvent, OnVariablePointerPressed, RoutingStrategies.Tunnel);
        VariableRows.AddHandler(InputElement.PointerMovedEvent, OnVariablePointerMoved, moveRelease, handledEventsToo: true);
        VariableRows.AddHandler(InputElement.PointerReleasedEvent, OnVariablePointerReleased, moveRelease, handledEventsToo: true);
        VariableRows.AddHandler(InputElement.PointerCaptureLostEvent, OnVariablePointerCaptureLost, RoutingStrategies.Direct);

        EnvironmentList.AddHandler(InputElement.PointerPressedEvent, OnListRightPointerPressed, RoutingStrategies.Tunnel);

        if (EnvListContextMenu is { } menu)
            menu.Opening += OnEnvListContextMenuOpening;
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

    // ─── Context menu ─────────────────────────────────────────────────────────

    private void OnListRightPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        var container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        _contextMenuEnvItem = container?.DataContext as EnvironmentListItemViewModel;

        if (_contextMenuEnvItem is not null)
            EnvironmentList.SelectedItem = _contextMenuEnvItem;
    }

    private void OnEnvListContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        menu.Items.Clear();

        if (_contextMenuEnvItem is null || _contextMenuEnvItem.IsGlobal)
        {
            e.Cancel = true;
            return;
        }

        var item = _contextMenuEnvItem;
        menu.Items.Add(MakeMenuItem("Rename", () => item.BeginRenameCommand.Execute(null)));
        if (item.CloneCommand.CanExecute(null))
            menu.Items.Add(MakeMenuItem("Clone", () => item.CloneCommand.Execute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("Delete", () => item.DeleteCommand.Execute(null), isDestructive: true));
    }

    private static MenuItem MakeMenuItem(string header, Action onClick, bool isDestructive = false)
    {
        var item = new MenuItem { Header = header };
        if (isDestructive)
            item.Foreground = Brush.Parse("#f48771");
        item.Click += (_, _) => onClick();
        return item;
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvironmentEditorViewModel.ShowResponseBodyConfig))
        {
            if (_trackedVm is null || !_trackedVm.ShowResponseBodyConfig)
                return;
            if (_trackedVm.PendingResponseBodyConfig is null)
                return;

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return;

            var dialog = new DynamicValueConfigDialog
            {
                DataContext = _trackedVm.PendingResponseBodyConfig,
            };

            await dialog.ShowDialog(owner);
            _trackedVm.OnResponseBodyConfigDialogClosed();
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
        _environmentOrderChanged = false;

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
            _ = EndDragAsync(e.Pointer);
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
            EnvironmentList.Cursor = new Cursor(StandardCursorType.Hand);
        }

        var hit = EnvironmentList.InputHitTest(currentPos) as Visual;
        var targetLbi = hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (targetLbi?.DataContext is not EnvironmentListItemViewModel targetItem) return;
        if (targetItem == _draggedItem || targetItem.IsRenaming) return;

        var items = vm.Environments;
        var currentIndex = items.IndexOf(_draggedItem);
        var targetIndex = items.IndexOf(targetItem);
        if (currentIndex < 0 || targetIndex < 0) return;

        var destinationIndex = ComputeDestinationIndex(currentIndex, targetIndex, e.GetPosition(targetLbi).Y, targetLbi.Bounds.Height);
        if (destinationIndex == currentIndex) return;

        if (vm.MoveEnvironment(_draggedItem, destinationIndex))
            _environmentOrderChanged = true;
    }

    private async void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
        => await EndDragAsync(e.Pointer).ConfigureAwait(true);

    private async void OnListPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        var shouldPersist = _environmentOrderChanged;
        _draggedItem = null;
        _isDragging = false;
        _environmentOrderChanged = false;
        EnvironmentList.Cursor = Cursor.Default;

        if (shouldPersist && DataContext is EnvironmentEditorViewModel vm)
            await vm.PersistEnvironmentOrderAsync().ConfigureAwait(true);
    }

    private async Task EndDragAsync(IPointer pointer)
    {
        if (_draggedItem is null) return;
        var shouldPersist = _environmentOrderChanged;
        _draggedItem = null;
        _isDragging = false;
        _environmentOrderChanged = false;
        EnvironmentList.Cursor = Cursor.Default;
        pointer.Capture(null);

        if (shouldPersist && DataContext is EnvironmentEditorViewModel vm)
            await vm.PersistEnvironmentOrderAsync().ConfigureAwait(true);
    }

    private void OnVariablePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedVariable = null;
        _isVariableDragging = false;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        if (TryGetVariableItem(e.Source as Visual, out var variable, out _))
        {
            _draggedVariable = variable;
            _variableDragStartPoint = e.GetPosition(VariableRows);
        }
    }

    private void OnVariablePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedVariable is null || DataContext is not EnvironmentEditorViewModel vm)
            return;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            EndVariableDrag(e.Pointer);
            return;
        }

        var selectedEnv = vm.SelectedEnvironment;
        if (selectedEnv is null)
            return;

        var currentPos = e.GetPosition(VariableRows);
        if (!_isVariableDragging)
        {
            if (Math.Abs(currentPos.Y - _variableDragStartPoint.Y) < DragThreshold) return;

            e.Pointer.Capture(VariableRows);
            _isVariableDragging = true;
            VariableRows.Cursor = new Cursor(StandardCursorType.Hand);
        }

        var hit = VariableRows.InputHitTest(currentPos) as Visual;
        if (!TryGetVariableItem(hit, out var targetVariable, out var targetVisual))
            return;
        if (targetVariable == _draggedVariable)
            return;

        var items = selectedEnv.Variables;
        var currentIndex = items.IndexOf(_draggedVariable);
        var targetIndex = items.IndexOf(targetVariable);
        if (currentIndex < 0 || targetIndex < 0) return;

        var destinationIndex = ComputeDestinationIndex(currentIndex, targetIndex, e.GetPosition(targetVisual).Y, targetVisual.Bounds.Height);
        if (destinationIndex == currentIndex) return;

        selectedEnv.MoveVariable(_draggedVariable, destinationIndex);
    }

    private void OnVariablePointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndVariableDrag(e.Pointer);

    private void OnVariablePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _draggedVariable = null;
        _isVariableDragging = false;
        VariableRows.Cursor = Cursor.Default;
    }

    private void EndVariableDrag(IPointer pointer)
    {
        if (_draggedVariable is null) return;
        _draggedVariable = null;
        _isVariableDragging = false;
        VariableRows.Cursor = Cursor.Default;
        pointer.Capture(null);
    }

    private static bool TryGetVariableItem(
        Visual? source,
        out EnvironmentVariableItemViewModel variable,
        out Visual visual)
    {
        var current = source;
        while (current is not null)
        {
            if (current is StyledElement element
                && element.DataContext is EnvironmentVariableItemViewModel item)
            {
                variable = item;
                visual = current;
                return true;
            }

            current = current.GetVisualParent();
        }

        variable = null!;
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
