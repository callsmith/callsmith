using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// A single editable row in the environment variable list.
/// Carries its own delete command so the view needs no parent binding.
/// Secret variables mask their value in the UI until revealed by toggling
/// <see cref="IsValueRevealed"/>.
/// </summary>
public sealed partial class EnvironmentVariableItemViewModel : ObservableObject
{
    private readonly Action<EnvironmentVariableItemViewModel> _onDelete;
    private readonly Action _onChanged;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isSecret;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>
    /// When <c>true</c> the plain-text value input is displayed even for secret variables.
    /// When <c>false</c> and <see cref="IsSecret"/> is <c>true</c>, a password input is shown.
    /// </summary>
    [ObservableProperty]
    private bool _isValueRevealed;

    /// <summary>Removes this row from its parent list when executed.</summary>
    public IRelayCommand DeleteCommand { get; }

    public EnvironmentVariableItemViewModel(
        Action<EnvironmentVariableItemViewModel> onDelete,
        Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        ArgumentNullException.ThrowIfNull(onChanged);
        _onDelete = onDelete;
        _onChanged = onChanged;
        DeleteCommand = new RelayCommand(() => _onDelete(this));
    }

    partial void OnNameChanged(string value) => _onChanged();
    partial void OnValueChanged(string value) => _onChanged();
    partial void OnIsSecretChanged(bool value) => _onChanged();
    partial void OnIsEnabledChanged(bool value) => _onChanged();
}
