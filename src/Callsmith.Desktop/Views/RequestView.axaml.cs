using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class RequestView : UserControl
{
    private RequestTabViewModel? _trackedVm;

    public RequestView()
    {
        InitializeComponent();
        CopyPreviewUrlButton.Click += OnCopyPreviewUrlClicked;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_trackedVm is not null)
            _trackedVm.PropertyChanged -= OnViewModelPropertyChanged;

        _trackedVm = DataContext as RequestTabViewModel;

        if (_trackedVm is not null)
            _trackedVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trackedVm is null) return;

        if (e.PropertyName == nameof(RequestTabViewModel.ShowSaveAsPanel))
        {
            if (!_trackedVm.ShowSaveAsPanel) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return;
            var dialog = new SaveAsDialog { DataContext = _trackedVm };
            await dialog.ShowDialog(owner);
        }
        else if (e.PropertyName == nameof(RequestTabViewModel.ShowDynamicValueConfig))
        {
            if (!_trackedVm.ShowDynamicValueConfig) return;
            if (_trackedVm.PendingDynamicConfig is null) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return;
            var dialog = new DynamicValueConfigDialog { DataContext = _trackedVm.PendingDynamicConfig };
            await dialog.ShowDialog(owner);
            _trackedVm.OnDynamicConfigDialogClosed();
        }
        else if (e.PropertyName == nameof(RequestTabViewModel.ShowMockDataConfig))
        {
            if (!_trackedVm.ShowMockDataConfig) return;
            if (_trackedVm.PendingMockDataConfig is null) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return;
            var dialog = new MockDataConfigDialog { DataContext = _trackedVm.PendingMockDataConfig };
            await dialog.ShowDialog(owner);
            _trackedVm.OnMockDataConfigDialogClosed();
        }
    }

    private async void OnCopyPreviewUrlClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequestTabViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && !string.IsNullOrEmpty(vm.PreviewUrl))
            await clipboard.SetTextAsync(vm.PreviewUrl);
    }
}

