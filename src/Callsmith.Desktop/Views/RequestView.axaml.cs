using Avalonia.Controls;
using Avalonia.Interactivity;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class RequestView : UserControl
{
    public RequestView()
    {
        InitializeComponent();
        CopyPreviewUrlButton.Click += OnCopyPreviewUrlClicked;
    }

    private async void OnCopyPreviewUrlClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequestViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && !string.IsNullOrEmpty(vm.PreviewUrl))
            await clipboard.SetTextAsync(vm.PreviewUrl);
    }
}
