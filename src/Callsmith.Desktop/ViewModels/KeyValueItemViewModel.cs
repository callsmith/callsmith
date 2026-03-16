using Callsmith.Desktop.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// A single editable row in a <see cref="KeyValueEditorViewModel"/>.
/// Carries its own delete command so the view needs no parent binding.
/// </summary>
public sealed partial class KeyValueItemViewModel : ObservableObject
{
    private readonly Action<KeyValueItemViewModel> _onDelete;

    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _showDeleteButton = true;

    [ObservableProperty]
    private bool _showEnabledToggle = true;

    /// <summary>
    /// Variable suggestions offered by the active environment. Bound to
    /// <c>controls:EnvVarCompletion.Suggestions</c> on the value TextBox.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<EnvVarSuggestion> _suggestionNames = [];

    /// <summary>Removes this row from its parent editor when executed.</summary>
    public IRelayCommand DeleteCommand { get; }

    public KeyValueItemViewModel(Action<KeyValueItemViewModel> onDelete)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        _onDelete = onDelete;
        DeleteCommand = new RelayCommand(() => _onDelete(this));
    }
}
