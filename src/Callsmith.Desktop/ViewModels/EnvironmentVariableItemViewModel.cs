using Callsmith.Core.Models;
using Callsmith.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// A single editable row in the environment variable list.
/// Variables have one of three types: static (plain text), mock-data (Bogus generator),
/// or response-body (extracted from a request response).
/// </summary>
public sealed partial class EnvironmentVariableItemViewModel : ObservableObject
{
    private readonly Action<EnvironmentVariableItemViewModel> _onDelete;
    private readonly Action _onChanged;
    private readonly Func<IReadOnlyDictionary<string, string>> _getVariables;
    private readonly Func<EnvironmentVariable?, Task<EnvironmentVariable?>> _editMockData;
    private readonly Func<EnvironmentVariable?, Task<EnvironmentVariable?>> _editResponseBody;

    // ── Observable state ─────────────────────────────────────────────────────

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(PreviewValue))]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isSecret;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatic))]
    [NotifyPropertyChangedFor(nameof(IsMockData))]
    [NotifyPropertyChangedFor(nameof(IsResponseBody))]
    [NotifyPropertyChangedFor(nameof(IsStaticNotSecret))]
    [NotifyPropertyChangedFor(nameof(IsStaticAndSecret))]
    [NotifyPropertyChangedFor(nameof(VariableTypeDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(PreviewValue))]
    private string _variableType = EnvironmentVariable.VariableTypes.Static;

    [ObservableProperty]
    private bool _isValueRevealed;

    // ── Mock-data properties ─────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MockDataSummary))]
    private string? _mockDataCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MockDataSummary))]
    private string? _mockDataField;

    public string MockDataSummary =>
        MockDataField is not null
            ? MockDataField
            : "Not configured";

    // ── Response-body properties ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseBodySummary))]
    private string? _responseRequestName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseBodySummary))]
    private string? _responsePath;

    [ObservableProperty]
    private DynamicFrequency _responseFrequency;

    [ObservableProperty]
    private int? _responseExpiresAfterSeconds;

    public string ResponseBodySummary =>
        ResponseRequestName is not null
            ? $"{LeafName(ResponseRequestName)} → {ResponsePath}"
            : "Not configured";

    /// <summary>Extracts the last path segment so folder prefixes are hidden in the UI.</summary>
    private static string LeafName(string name)
    {
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    // ── Type helpers ─────────────────────────────────────────────────────────

    /// <summary>Display labels shown in the type dropdown, in order.</summary>
    public static readonly string[] VariableTypeOptions =
        ["Static", "Mock Data", "Response Body Value"];

    public bool IsStatic => VariableType == EnvironmentVariable.VariableTypes.Static;
    public bool IsMockData => VariableType == EnvironmentVariable.VariableTypes.MockData;
    public bool IsResponseBody => VariableType == EnvironmentVariable.VariableTypes.ResponseBody;
    public bool IsStaticNotSecret => IsStatic && !IsSecret;
    public bool IsStaticAndSecret  => IsStatic && IsSecret;

    /// <summary>Human-readable label for the currently selected type; two-way bound to the dropdown.</summary>
    public string VariableTypeDisplay
    {
        get => VariableType switch
        {
            EnvironmentVariable.VariableTypes.MockData     => "Mock Data",
            EnvironmentVariable.VariableTypes.ResponseBody => "Response Body Value",
            _                                              => "Static",
        };
        set
        {
            // Avalonia's ComboBox writes null to SelectedItem during layout initialisation
            // before it resolves the actual selection. Ignore it to prevent spurious dirty marking.
            if (value is null) return;
            VariableType = value switch
            {
                "Mock Data"           => EnvironmentVariable.VariableTypes.MockData,
                "Response Body Value" => EnvironmentVariable.VariableTypes.ResponseBody,
                _                     => EnvironmentVariable.VariableTypes.Static,
            };
        }
    }

    // ── Preview (static vars with {{token}} references) ──────────────────────

    public string? PreviewValue
    {
        get
        {
            if (!IsStatic || !Value.Contains("{{")) return null;
            return VariableSubstitutionService.Substitute(Value, _getVariables());
        }
    }

    public bool HasPreview => IsStatic && !IsSecret && Value.Contains("{{");

    public IRelayCommand DeleteCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public EnvironmentVariableItemViewModel(
        Action<EnvironmentVariableItemViewModel> onDelete,
        Action onChanged,
        Func<IReadOnlyDictionary<string, string>> getVariables,
        Func<EnvironmentVariable?, Task<EnvironmentVariable?>>? editMockData = null,
        Func<EnvironmentVariable?, Task<EnvironmentVariable?>>? editResponseBody = null)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(getVariables);
        _onDelete = onDelete;
        _onChanged = onChanged;
        _getVariables = getVariables;
        _editMockData = editMockData ?? (_ => Task.FromResult<EnvironmentVariable?>(null));
        _editResponseBody = editResponseBody ?? (_ => Task.FromResult<EnvironmentVariable?>(null));
        DeleteCommand = new RelayCommand(() => _onDelete(this));
    }

    // ── Type selection ────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectStaticType()
    {
        VariableType = EnvironmentVariable.VariableTypes.Static;
        _onChanged();
    }

    [RelayCommand]
    private async Task SelectMockDataTypeAsync()
    {
        VariableType = EnvironmentVariable.VariableTypes.MockData;
        await OpenMockDataConfigAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SelectResponseBodyTypeAsync()
    {
        VariableType = EnvironmentVariable.VariableTypes.ResponseBody;
        await OpenResponseBodyConfigAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenMockDataConfigAsync()
    {
        var current = BuildModel();
        var result = await _editMockData(current).ConfigureAwait(true);
        if (result is null) return;
        var changed = result.MockDataCategory != MockDataCategory
                   || result.MockDataField    != MockDataField;
        MockDataCategory = result.MockDataCategory;
        MockDataField    = result.MockDataField;
        if (changed) _onChanged();
    }

    [RelayCommand]
    private async Task OpenResponseBodyConfigAsync()
    {
        var current = BuildModel();
        var result = await _editResponseBody(current).ConfigureAwait(true);
        if (result is null) return;
        var changed = result.ResponseRequestName       != ResponseRequestName
                   || result.ResponsePath              != ResponsePath
                   || result.ResponseFrequency         != ResponseFrequency
                   || result.ResponseExpiresAfterSeconds != ResponseExpiresAfterSeconds;
        ResponseRequestName        = result.ResponseRequestName;
        ResponsePath               = result.ResponsePath;
        ResponseFrequency          = result.ResponseFrequency;
        ResponseExpiresAfterSeconds = result.ResponseExpiresAfterSeconds;
        if (changed) _onChanged();
    }

    // ── Build / load ──────────────────────────────────────────────────────────

    public EnvironmentVariable BuildModel() => new()
    {
        Name = Name.Trim(),
        Value = Value,
        VariableType = VariableType,
        IsSecret = IsSecret,
        MockDataCategory = MockDataCategory,
        MockDataField = MockDataField,
        ResponseRequestName = ResponseRequestName,
        ResponsePath = ResponsePath,
        ResponseFrequency = ResponseFrequency,
        ResponseExpiresAfterSeconds = ResponseExpiresAfterSeconds,
    };

    // ── Property change hooks ────────────────────────────────────────────────

    partial void OnNameChanged(string value) => _onChanged();
    partial void OnValueChanged(string value) => _onChanged();
    partial void OnVariableTypeChanged(string value) => _onChanged();

    partial void OnIsSecretChanged(bool value)
    {
        _onChanged();
        OnPropertyChanged(nameof(IsStaticNotSecret));
        OnPropertyChanged(nameof(IsStaticAndSecret));
    }

    public void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewValue));
        OnPropertyChanged(nameof(HasPreview));
    }
}
