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
        SidebarSplitter.AddHandler(PointerReleasedEvent, OnSidebarSplitterPointerReleased, handledEventsToo: true);
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
            ApplySidebarFraction(_trackedVm.RequestTreeSplitterFraction);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.RequestTreeSplitterFraction) && _trackedVm is not null)
        {
            var fraction = _trackedVm.RequestTreeSplitterFraction;
            Dispatcher.UIThread.Post(() => ApplySidebarFraction(fraction));
        }
    }

    private void ApplySidebarFraction(double? fraction)
    {
        if (!fraction.HasValue) return;
        if (MainGrid.ColumnDefinitions.Count < 3) return;
        var f = fraction.Value;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(f, GridUnitType.Star);
        MainGrid.ColumnDefinitions[2].Width = new GridLength(1 - f, GridUnitType.Star);
    }

    private void OnSidebarSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_trackedVm is null) return;
        var vm = _trackedVm;
        Dispatcher.UIThread.Post(() =>
        {
            if (MainGrid.ColumnDefinitions.Count < 3) return;
            var left = MainGrid.ColumnDefinitions[0].ActualWidth;
            var right = MainGrid.ColumnDefinitions[2].ActualWidth;
            var total = left + right;
            if (total > 0)
                vm.OnRequestTreeSplitterMoved(left / total);
        });
    }
}