using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class SequencesView : UserControl
{
    private SequenceStepViewModel? _draggedStep;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6.0;

    public SequencesView()
    {
        InitializeComponent();

        var moveRelease = RoutingStrategies.Direct | RoutingStrategies.Bubble;
        StepRows.AddHandler(InputElement.PointerPressedEvent, OnStepPointerPressed, RoutingStrategies.Tunnel);
        StepRows.AddHandler(InputElement.PointerMovedEvent, OnStepPointerMoved, moveRelease, handledEventsToo: true);
        StepRows.AddHandler(InputElement.PointerReleasedEvent, OnStepPointerReleased, moveRelease, handledEventsToo: true);
        StepRows.AddHandler(InputElement.PointerCaptureLostEvent, OnStepPointerCaptureLost, RoutingStrategies.Direct);
    }

    private void OnStepPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedStep = null;
        _isDragging = false;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        if (TryGetStepItem(e.Source as Visual, out var step))
        {
            _draggedStep = step;
            _dragStartPoint = e.GetPosition(StepRows);
        }
    }

    private void OnStepPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedStep is null || StepRows.DataContext is not SequenceEditorViewModel vm) return;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            EndStepDrag(e.Pointer);
            return;
        }

        var currentPos = e.GetPosition(StepRows);
        if (!_isDragging)
        {
            if (Math.Abs(currentPos.Y - _dragStartPoint.Y) < DragThreshold) return;

            e.Pointer.Capture(StepRows);
            _isDragging = true;
            StepRows.Cursor = new Cursor(StandardCursorType.Hand);
        }

        var hit = StepRows.InputHitTest(currentPos) as Visual;
        if (!TryGetStepItem(hit, out var targetStep)) return;
        if (targetStep == _draggedStep) return;

        var steps = vm.Steps;
        var currentIndex = steps.IndexOf(_draggedStep);
        var targetIndex = steps.IndexOf(targetStep);
        if (currentIndex < 0 || targetIndex < 0) return;

        if (!TryGetStepRowVisual(hit, out var targetVisual)) return;

        var destinationIndex = ComputeDestinationIndex(
            currentIndex,
            targetIndex,
            e.GetPosition(targetVisual).Y,
            targetVisual.Bounds.Height);

        if (destinationIndex == currentIndex) return;
        vm.MoveStep(_draggedStep, destinationIndex);
    }

    private void OnStepPointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndStepDrag(e.Pointer);

    private void OnStepPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _draggedStep = null;
        _isDragging = false;
        StepRows.Cursor = Cursor.Default;
    }

    private void EndStepDrag(IPointer pointer)
    {
        if (_draggedStep is null) return;
        _draggedStep = null;
        _isDragging = false;
        StepRows.Cursor = Cursor.Default;
        pointer.Capture(null);
    }

    private static bool TryGetStepItem(Visual? source, out SequenceStepViewModel step)
    {
        var current = source;
        while (current is not null)
        {
            if (current is StyledElement element && element.DataContext is SequenceStepViewModel vm)
            {
                step = vm;
                return true;
            }
            current = current.GetVisualParent();
        }

        step = null!;
        return false;
    }

    private static bool TryGetStepRowVisual(Visual? source, out Visual visual)
    {
        var current = source;
        while (current is not null)
        {
            if (current is StyledElement element && element.DataContext is SequenceStepViewModel)
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
