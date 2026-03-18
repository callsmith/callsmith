using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Callsmith.Desktop.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.CommandPaletteViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ViewModels.CommandPaletteViewModel.IsOpen)
                    && vm.IsOpen)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => SearchBox.Focus(),
                        Avalonia.Threading.DispatcherPriority.Input);
                }
            };
        }
    }

    // Dismiss when clicking on the dark scrim outside the card.
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.CommandPaletteViewModel vm)
            vm.CloseCommand.Execute(null);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.CommandPaletteViewModel vm) return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Up:
                vm.SelectPrevious();
                EnsureSelectedVisible();
                e.Handled = true;
                break;

            case Key.Down:
                vm.SelectNext();
                EnsureSelectedVisible();
                e.Handled = true;
                break;

            case Key.Enter:
                _ = vm.ConfirmSelectionAsync();
                e.Handled = true;
                break;
        }
    }

    private void OnResultPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ViewModels.CommandPaletteViewModel vm) return;
        if (sender is not Border { DataContext: ViewModels.CommandPaletteResult result }) return;

        vm.SelectedResult = result;
        _ = vm.ConfirmSelectionAsync();
        e.Handled = true;
    }

    private void OnResultPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not ViewModels.CommandPaletteViewModel vm) return;
        if (sender is Border { DataContext: ViewModels.CommandPaletteResult result })
            vm.SelectedResult = result;
    }

    /// <summary>
    /// Synchronises the visual selected state on result rows by adding/removing
    /// the <c>palette-selected</c> style class, and scrolls the selected row into view.
    /// </summary>
    private void EnsureSelectedVisible()
    {
        if (DataContext is not ViewModels.CommandPaletteViewModel vm) return;

        // Walk every Border child inside the ItemsControl to apply/remove the selection class.
        foreach (var border in ResultsList.GetVisualDescendants().OfType<Border>()
                     .Where(b => b.Name == "ResultRow"))
        {
            var isSelected = border.DataContext is ViewModels.CommandPaletteResult r
                             && r.Equals(vm.SelectedResult);
            if (isSelected)
                border.Classes.Add("palette-selected");
            else
                border.Classes.Remove("palette-selected");
        }
    }
}
