using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class HistoryPanelView : UserControl
{
    private HistoryEntryRowViewModel? _contextMenuEntry;

    public HistoryPanelView()
    {
        InitializeComponent();
        HistoryEntriesList.AddHandler(InputElement.PointerPressedEvent, OnHistoryEntriesPointerPressed, RoutingStrategies.Tunnel);

        if (HistoryEntryContextMenu is { } menu)
            menu.Opening += OnHistoryEntryContextMenuOpening;
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
