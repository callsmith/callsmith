using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;
using System.ComponentModel;

namespace Callsmith.Desktop.Views;

public partial class HistoryPanelView : UserControl
{
    private HistoryEntryRowViewModel? _contextMenuEntry;
    private HistoryPanelViewModel? _trackedVm;

    public HistoryPanelView()
    {
        InitializeComponent();
        HistoryEntriesList.AddHandler(InputElement.PointerPressedEvent, OnHistoryEntriesPointerPressed, RoutingStrategies.Tunnel);

        AddHandler(InputElement.PointerPressedEvent, OnMouseBackButtonPressed, RoutingStrategies.Tunnel);

        if (HistoryEntryContextMenu is { } menu)
            menu.Opening += OnHistoryEntryContextMenuOpening;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_trackedVm is not null)
            _trackedVm.PropertyChanged -= OnViewModelPropertyChanged;

        _trackedVm = DataContext as HistoryPanelViewModel;

        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyDetailLayout(_trackedVm.IsHorizontalDetailLayout);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryPanelViewModel.IsHorizontalDetailLayout) && _trackedVm is not null)
            ApplyDetailLayout(_trackedVm.IsHorizontalDetailLayout);
    }

    /// <summary>
    /// Rearranges <see cref="DetailContentGrid"/> children between horizontal
    /// (side-by-side) and vertical (stacked) layout without duplicating AXAML content.
    /// </summary>
    private void ApplyDetailLayout(bool isHorizontal)
    {
        if (isHorizontal)
        {
            DetailContentGrid.RowDefinitions.Clear();
            DetailContentGrid.ColumnDefinitions = new ColumnDefinitions("0.45*,5,0.55*");

            Grid.SetRow(DetailRequestPanel, 0);
            Grid.SetColumn(DetailRequestPanel, 0);

            Grid.SetRow(DetailContentSplitter, 0);
            Grid.SetColumn(DetailContentSplitter, 1);
            DetailContentSplitter.ResizeDirection = GridResizeDirection.Columns;
            DetailContentSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            DetailContentSplitter.VerticalAlignment = VerticalAlignment.Stretch;

            Grid.SetRow(DetailResponsePanel, 0);
            Grid.SetColumn(DetailResponsePanel, 2);
        }
        else
        {
            DetailContentGrid.ColumnDefinitions.Clear();
            DetailContentGrid.RowDefinitions = new RowDefinitions("0.45*,5,0.55*");

            Grid.SetRow(DetailRequestPanel, 0);
            Grid.SetColumn(DetailRequestPanel, 0);

            Grid.SetRow(DetailContentSplitter, 1);
            Grid.SetColumn(DetailContentSplitter, 0);
            DetailContentSplitter.ResizeDirection = GridResizeDirection.Rows;
            DetailContentSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            DetailContentSplitter.VerticalAlignment = VerticalAlignment.Stretch;

            Grid.SetRow(DetailResponsePanel, 2);
            Grid.SetColumn(DetailResponsePanel, 0);
        }
    }
    
    private const double LoadMoreTriggerDistance = 240d;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = Dispatcher.UIThread.InvokeAsync(AttachHistoryEntriesScrollViewer, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachHistoryEntriesScrollViewer();
    }

    private void AttachHistoryEntriesScrollViewer()
    {
        var scrollViewer = HistoryEntriesList.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is not null)
            scrollViewer.ScrollChanged += OnHistoryEntriesScrollChanged;
    }

    private void DetachHistoryEntriesScrollViewer()
    {
        var scrollViewer = HistoryEntriesList.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is not null)
            scrollViewer.ScrollChanged -= OnHistoryEntriesScrollChanged;
    }

    private void OnHistoryEntriesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || DataContext is not HistoryPanelViewModel vm)
            return;

        var remainingDistance = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;
        if (remainingDistance < LoadMoreTriggerDistance && vm.HasMoreEntries && !vm.IsIncrementalLoading)
        {
            _ = vm.EnsureMoreEntriesAsync();
        }
    }

    private void OnMouseBackButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.PointerUpdateKind != PointerUpdateKind.XButton1Pressed) return;
        if (DataContext is not HistoryPanelViewModel vm) return;
        vm.CloseCommand.Execute(null);
        e.Handled = true;
    }

    private void OnHistoryEntriesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        var container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        _contextMenuEntry = container?.DataContext as HistoryEntryRowViewModel;

        if (_contextMenuEntry is not null)
            HistoryEntriesList.SelectedItem = _contextMenuEntry;
    }

    private void OnHistoryEntryContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not HistoryPanelViewModel vm || sender is not ContextMenu menu)
            return;

        menu.Items.Clear();

        if (_contextMenuEntry is null)
            return;

        if (_contextMenuEntry.Entry.RequestId is not null && !vm.IsRequestScoped)
        {
            menu.Items.Add(MakeMenuItem("Scope to this request", () => vm.ScopeToRequestCommand.Execute(_contextMenuEntry)));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(MakeMenuItem("Remove entry from history", () =>
            vm.RequestRemoveEntryFromHistoryCommand.Execute(_contextMenuEntry), isDestructive: true));
    }

    private static MenuItem MakeMenuItem(string header, Action onClick, bool isDestructive = false)
    {
        var item = new MenuItem { Header = header };
        if (isDestructive)
            item.Foreground = Brushes.IndianRed;
        item.Click += (_, _) => onClick();
        return item;
    }

}
