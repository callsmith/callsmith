using System.ComponentModel;
using Avalonia.Controls;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class SaveAsDialog : Window
{
    public SaveAsDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is RequestTabViewModel vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RequestTabViewModel.ShowSaveAsPanel)
            && DataContext is RequestTabViewModel { ShowSaveAsPanel: false })
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is RequestTabViewModel vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
