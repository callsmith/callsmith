using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Callsmith.Desktop.ViewModels;
using System.ComponentModel;

namespace Callsmith.Desktop.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _trackedVm;

    public MainWindow()
    {
        InitializeComponent();
        SidebarSplitter.PointerReleased += OnSidebarSplitterPointerReleased;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_trackedVm is not null)
            _trackedVm.PropertyChanged -= OnViewModelPropertyChanged;

        _trackedVm = DataContext as MainWindowViewModel;

        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged += OnViewModelPropertyChanged;
            ApplySidebarPosition(_trackedVm.RequestTreeSplitterPosition);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.RequestTreeSplitterPosition) && _trackedVm is not null)
        {
            var position = _trackedVm.RequestTreeSplitterPosition;
            Dispatcher.UIThread.Post(() => ApplySidebarPosition(position));
        }
    }

    private void ApplySidebarPosition(double? position)
    {
        if (!position.HasValue) return;
        if (MainGrid.ColumnDefinitions.Count > 0)
            MainGrid.ColumnDefinitions[0].Width = new GridLength(position.Value, GridUnitType.Pixel);
    }

    private void OnSidebarSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_trackedVm is null) return;
        var vm = _trackedVm;
        Dispatcher.UIThread.Post(() =>
        {
            var width = MainGrid.ColumnDefinitions.Count > 0 ? MainGrid.ColumnDefinitions[0].ActualWidth : 0;
            if (width > 0)
                vm.OnRequestTreeSplitterMoved(width);
        });
    }
}