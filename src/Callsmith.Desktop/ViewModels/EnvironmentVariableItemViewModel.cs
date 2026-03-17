using Callsmith.Core.Services;
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
    private readonly Func<IReadOnlyDictionary<string, string>> _getVariables;

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
        Action onChanged,
        Func<IReadOnlyDictionary<string, string>> getVariables)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(getVariables);
        _onDelete = onDelete;
        _onChanged = onChanged;
        _getVariables = getVariables;
        DeleteCommand = new RelayCommand(() => _onDelete(this));
    }

    /// <summary>
    /// Resolved preview of <see cref="Value"/> with <c>{{VAR}}</c> tokens substituted.
    /// Returns <see langword="null"/> when the value contains no token references.
    /// </summary>
    public string? PreviewValue
    {
        get
        {
            if (!Value.Contains("{{")) return null;
            return VariableSubstitutionService.Substitute(Value, _getVariables());
        }
    }

    /// <summary>
    /// True when the value contains variable references and a preview is available to show.
    /// Secret values are excluded to avoid leaking resolved secrets in plain text.
    /// </summary>
    public bool HasPreview => !IsSecret && Value.Contains("{{");

    /// <summary>Notifies bindings that <see cref="PreviewValue"/> and <see cref="HasPreview"/> may have changed.</summary>
    public void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewValue));
        OnPropertyChanged(nameof(HasPreview));
    }

    partial void OnNameChanged(string value) => _onChanged();
    partial void OnValueChanged(string value) => _onChanged();
    partial void OnIsSecretChanged(bool value) => _onChanged();
    partial void OnIsEnabledChanged(bool value) => _onChanged();
}
