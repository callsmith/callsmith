using Avalonia.Controls;
using Avalonia.Interactivity;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class CurlDialog : Window
{
    public CurlDialog()
    {
        InitializeComponent();
        CopyButton.Click += OnCopyClicked;
        CloseButton.Click += OnCloseClicked;
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not CurlDialogViewModel vm) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(vm.CurlCommandText);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CurlDialog] OnCopyClicked error: {ex}");
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
