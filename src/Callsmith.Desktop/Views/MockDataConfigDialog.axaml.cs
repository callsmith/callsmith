using Avalonia.Controls;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class MockDataConfigDialog : Window
{
    public MockDataConfigDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MockDataConfigViewModel vm)
            vm.CloseRequested += OnVmCloseRequested;
    }

    private void OnVmCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MockDataConfigViewModel vm)
            vm.CloseRequested -= OnVmCloseRequested;
    }
}
