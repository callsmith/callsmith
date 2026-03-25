using Avalonia.Controls;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class ImportCollectionDialog : Window
{
    private ImportCollectionViewModel? _trackedVm;

    public ImportCollectionDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_trackedVm is not null)
            _trackedVm.CloseRequested -= OnVmCloseRequested;

        _trackedVm = DataContext as ImportCollectionViewModel;

        if (_trackedVm is not null)
            _trackedVm.CloseRequested += OnVmCloseRequested;
    }

    private void OnVmCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_trackedVm is not null)
            _trackedVm.CloseRequested -= OnVmCloseRequested;
    }
}
