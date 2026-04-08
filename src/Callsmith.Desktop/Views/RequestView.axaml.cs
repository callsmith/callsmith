using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Callsmith.Desktop.ViewModels;

namespace Callsmith.Desktop.Views;

public partial class RequestView : UserControl
{
    private RequestTabViewModel? _trackedVm;

    public RequestView()
    {
        InitializeComponent();
        CopyPreviewUrlButton.Click += OnCopyPreviewUrlClicked;
        ContentSplitter.PointerReleased += OnContentSplitterPointerReleased;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_trackedVm is not null)
            _trackedVm.PropertyChanged -= OnViewModelPropertyChanged;

        _trackedVm = DataContext as RequestTabViewModel;

        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyLayout(_trackedVm.IsHorizontalLayout, _trackedVm.SplitterPosition);

            // Inject the platform file picker callback — the ViewModel has no Avalonia reference.
            _trackedVm.OpenFilePickerFunc = async (ct) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null) return null;
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions { AllowMultiple = false });
                if (files.Count == 0) return null;
                await using var stream = await files[0].OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                var localPath = files[0].TryGetLocalPath();
                var displayPath = localPath ?? files[0].Name;
                return (ms.ToArray(), files[0].Name, displayPath);
            };
        }
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trackedVm is null) return;

        if (e.PropertyName == nameof(RequestTabViewModel.IsHorizontalLayout))
        {
            ApplyLayout(_trackedVm.IsHorizontalLayout, _trackedVm.SplitterPosition);
        }
        else if (e.PropertyName == nameof(RequestTabViewModel.SplitterPosition))
        {
            ApplySplitterPosition(_trackedVm.IsHorizontalLayout, _trackedVm.SplitterPosition);
        }
        else if (e.PropertyName == nameof(RequestTabViewModel.ShowSaveAsPanel))
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
        else if (e.PropertyName == nameof(RequestTabViewModel.ShowCurlDialog))
        {
            if (!_trackedVm.ShowCurlDialog) return;
            var request = _trackedVm.CurlRequestSnapshot;
            var authMask = _trackedVm.CurlAuthMask;
            var secretValues = _trackedVm.CurlSecretValues;
            // Reset immediately so the property change can fire again on re-open.
            _trackedVm.ShowCurlDialog = false;
            if (request is null) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return;
            var dialog = new CurlDialog
            {
                DataContext = new CurlDialogViewModel(request, authMask, secretValues)
            };
            await dialog.ShowDialog(owner);
        }
    }

    /// <summary>
    /// Rearranges <see cref="ContentGrid"/> children between vertical (stacked) and
    /// horizontal (side-by-side) layout without duplicating any AXAML content.
    /// If <paramref name="splitterPosition"/> is provided it is applied as a pixel size
    /// for the first panel instead of the default star-ratio.
    /// </summary>
    private void ApplyLayout(bool isHorizontal, double? splitterPosition = null)
    {
        if (isHorizontal)
        {
            ContentGrid.RowDefinitions.Clear();
            ContentGrid.ColumnDefinitions = new ColumnDefinitions("0.45*,6,0.55*");

            Grid.SetRow(RequestConfigPanel, 0);
            Grid.SetColumn(RequestConfigPanel, 0);

            Grid.SetRow(ContentSplitter, 0);
            Grid.SetColumn(ContentSplitter, 1);
            ContentSplitter.ResizeDirection = GridResizeDirection.Columns;
            ContentSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            ContentSplitter.VerticalAlignment = VerticalAlignment.Stretch;

            Grid.SetRow(ResponsePanel, 0);
            Grid.SetColumn(ResponsePanel, 2);

            if (splitterPosition.HasValue)
                ContentGrid.ColumnDefinitions[0].Width = new GridLength(splitterPosition.Value, GridUnitType.Pixel);
        }
        else
        {
            ContentGrid.ColumnDefinitions.Clear();
            ContentGrid.RowDefinitions = new RowDefinitions("0.45*,6,0.55*");

            Grid.SetRow(RequestConfigPanel, 0);
            Grid.SetColumn(RequestConfigPanel, 0);

            Grid.SetRow(ContentSplitter, 1);
            Grid.SetColumn(ContentSplitter, 0);
            ContentSplitter.ResizeDirection = GridResizeDirection.Rows;
            ContentSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            ContentSplitter.VerticalAlignment = VerticalAlignment.Stretch;

            Grid.SetRow(ResponsePanel, 2);
            Grid.SetColumn(ResponsePanel, 0);

            if (splitterPosition.HasValue)
                ContentGrid.RowDefinitions[0].Height = new GridLength(splitterPosition.Value, GridUnitType.Pixel);
        }
    }

    /// <summary>
    /// Applies a saved pixel position to the already-arranged grid without re-running the
    /// full layout rearrangement (used when only the position changes, not the orientation).
    /// </summary>
    private void ApplySplitterPosition(bool isHorizontal, double? splitterPosition)
    {
        if (!splitterPosition.HasValue) return;
        if (isHorizontal && ContentGrid.ColumnDefinitions.Count > 0)
            ContentGrid.ColumnDefinitions[0].Width = new GridLength(splitterPosition.Value, GridUnitType.Pixel);
        else if (!isHorizontal && ContentGrid.RowDefinitions.Count > 0)
            ContentGrid.RowDefinitions[0].Height = new GridLength(splitterPosition.Value, GridUnitType.Pixel);
    }

    private void OnContentSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_trackedVm is null) return;
        var vm = _trackedVm;
        var isHorizontal = vm.IsHorizontalLayout;
        var pos = isHorizontal
            ? RequestConfigPanel.Bounds.Width
            : RequestConfigPanel.Bounds.Height;
        if (pos > 0)
            vm.SplitterChangedCallback?.Invoke(pos);
    }

    private async void OnCopyPreviewUrlClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequestTabViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && !string.IsNullOrEmpty(vm.PreviewUrl))
            await clipboard.SetTextAsync(vm.PreviewUrl);
    }

    /// <summary>
    /// Handles the body-type ComboBox selection change.  The binding is OneWay (VM → View)
    /// to prevent Avalonia's TwoWay binding from writing back intermediate values during
    /// DataContext switches, which would spuriously mark the tab dirty.
    /// This handler provides the View → VM direction, but only for genuine user selections
    /// (i.e. it ignores events that fire as a result of the binding updating the control).
    /// </summary>
    private void OnBodyTypeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not RequestTabViewModel vm) return;
        if (sender is not ComboBox cb) return;
        if (cb.SelectedItem is not BodyTypeOption opt || opt.IsSeparator) return;

        // When the OneWay binding updates the ComboBox (e.g. on DataContext change or
        // after LoadRequest), SelectedItem is set to the value already held by the VM.
        // Skip those no-op echoes so only true user clicks reach the ViewModel.
        if (opt.Value == vm.SelectedBodyType) return;

        vm.SelectedBodyTypeOption = opt;
    }
}

