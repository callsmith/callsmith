using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;
using System.ComponentModel;

namespace Callsmith.Desktop.Views;

public partial class HistoryPanelView : UserControl
{
    private HistoryEntryRowViewModel? _contextMenuEntry;
    private HistoryPanelViewModel? _trackedVm;
    private ScrollViewer? _historyScrollViewer;

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

            // Inject the platform save-file callback — the ViewModel has no Avalonia reference.
            _trackedVm.SaveFileFunc = async (bytes, suggestedName, ct) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null) return;
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions { SuggestedFileName = suggestedName });
                if (file is null) return;
                await using var stream = await file.OpenWriteAsync();
                await stream.WriteAsync(bytes, ct);
            };
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryPanelViewModel.IsHorizontalDetailLayout) && _trackedVm is not null)
        {
            // Preferences are loaded asynchronously with ConfigureAwait(false), so
            // this event may arrive on a thread-pool thread. Grid manipulation must
            // happen on the UI thread, so always dispatch there.
            var isHorizontal = _trackedVm.IsHorizontalDetailLayout;
            Dispatcher.UIThread.Post(() => ApplyDetailLayout(isHorizontal));
        }
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
        HistoryEntriesList.TemplateApplied += OnHistoryEntriesListTemplateApplied;
        _ = Dispatcher.UIThread.InvokeAsync(TryAttachHistoryEntriesScrollViewer, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HistoryEntriesList.TemplateApplied -= OnHistoryEntriesListTemplateApplied;
        DetachHistoryEntriesScrollViewer();
    }

    private void OnHistoryEntriesListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        TryAttachHistoryEntriesScrollViewer();
    }

    private void TryAttachHistoryEntriesScrollViewer()
    {
        DetachHistoryEntriesScrollViewer();
        _historyScrollViewer = HistoryEntriesList.FindDescendantOfType<ScrollViewer>();
        if (_historyScrollViewer is not null)
            _historyScrollViewer.ScrollChanged += OnHistoryEntriesScrollChanged;
    }

    private void DetachHistoryEntriesScrollViewer()
    {
        if (_historyScrollViewer is not null)
        {
            _historyScrollViewer.ScrollChanged -= OnHistoryEntriesScrollChanged;
            _historyScrollViewer = null;
        }
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

        var canOpen = vm.CanOpenEntryRequest(_contextMenuEntry);
        var openRequestItem = MakeMenuItem("Go to request",
            () => vm.OpenRequestFromEntryCommand.Execute(_contextMenuEntry));
        if (!canOpen)
        {
            openRequestItem.IsEnabled = false;
            openRequestItem.Cursor = new Cursor(StandardCursorType.Help);
            ToolTip.SetTip(openRequestItem, "Request not found");
        }
        menu.Items.Add(openRequestItem);

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
