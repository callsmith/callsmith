using Avalonia.Controls;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class FolderSettingsDialog : Window
{
    public FolderSettingsDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FolderSettingsViewModel vm)
            vm.CloseRequested += OnVmCloseRequested;
    }

    private void OnVmCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is FolderSettingsViewModel vm)
            vm.CloseRequested -= OnVmCloseRequested;
    }
}
