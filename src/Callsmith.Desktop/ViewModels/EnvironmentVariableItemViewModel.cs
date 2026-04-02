using Avalonia.Threading;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Controls;
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
    private readonly Action<string> _onInvalidateDynamicPreviewCache;
    private readonly Action<string, string> _onUpdateDynamicPreviewCache;
    private readonly Func<ResolvedEnvironment> _getResolvedEnv;
    private readonly Func<EnvironmentVariable?, Task<EnvironmentVariable?>> _editMockData;
    private readonly Func<EnvironmentVariable?, Task<EnvironmentVariable?>> _editResponseBody;

    // Debounce CTS — prevent "Resolving…" from flashing on fast resolution.
    private CancellationTokenSource? _dynamicLoadingDelayCts;

    /// <summary>
    /// True if this variable belongs to a concrete (non-global) Bruno environment.
    /// Bruno only supports static variables in concrete environments.
    /// </summary>
    public bool IsBrunoConcreteEnvironment { get; set; }

    /// <summary>
    /// True if this variable belongs to the global environment.
    /// Controls visibility of the "force override" checkbox in the UI.
    /// </summary>
    public bool IsGlobal { get; set; }

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
    private bool _isForceGlobalOverride;

    /// <summary>True when this variable has a name collision with a variable in the other environment tier.</summary>
    [ObservableProperty]
    private bool _isOverridden;

    /// <summary>Tooltip text explaining the override/collision when <see cref="IsOverridden"/> is true.</summary>
    [ObservableProperty]
    private string? _overrideTooltip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatic))]
    [NotifyPropertyChangedFor(nameof(IsMockData))]
    [NotifyPropertyChangedFor(nameof(IsResponseBody))]
    [NotifyPropertyChangedFor(nameof(IsStaticNotSecret))]
    [NotifyPropertyChangedFor(nameof(IsStaticAndSecret))]
    [NotifyPropertyChangedFor(nameof(CanBeSecret))]
    [NotifyPropertyChangedFor(nameof(SecretLockTooltip))]
    [NotifyPropertyChangedFor(nameof(VariableTypeDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(PreviewValue))]
    private string _variableType = EnvironmentVariable.VariableTypes.Static;

    [ObservableProperty]
    private bool _isValueRevealed;

    [ObservableProperty]
    private IReadOnlyList<EnvVarSuggestion> _suggestionNames = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValue))]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(IsPreviewValueVisible))]
    private string? _dynamicPreviewValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(IsPreviewValueVisible))]
    private bool _isDynamicPreviewLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(IsPreviewValueVisible))]
    private bool _isDynamicPreviewError;

    // ── Mock-data properties ─────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MockDataSummary))]
    [NotifyPropertyChangedFor(nameof(PreviewValue))]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private string? _mockDataCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MockDataSummary))]
    [NotifyPropertyChangedFor(nameof(PreviewValue))]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
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
    [NotifyPropertyChangedFor(nameof(ResponseBodySummary))]
    private ResponseValueMatcher _responseMatcher = ResponseValueMatcher.JsonPath;

    [ObservableProperty]
    private DynamicFrequency _responseFrequency;

    [ObservableProperty]
    private int? _responseExpiresAfterSeconds;

    public string ResponseBodySummary =>
        ResponseRequestName is not null
            ? $"{LeafName(ResponseRequestName)} [{ResponseMatcher}] → {ResponsePath}"
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

    /// <summary>
    /// Instance property that returns available type options for this variable.
    /// For concrete Bruno environments, only Static is available.
    /// </summary>
    public string[] AvailableVariableTypeOptions =>
        IsBrunoConcreteEnvironment
            ? ["Static"]
            : VariableTypeOptions;

    /// <summary>
    /// True if the type dropdown should be disabled (concrete Bruno environment).
    /// </summary>
    public bool IsTypeDropdownDisabled => IsBrunoConcreteEnvironment;

    /// <summary>
    /// Tooltip for the Type combobox
    /// </summary>
    public string TypeComboBoxToolTip => IsBrunoConcreteEnvironment
        ? "Bruno only supports static environment variables"
        : "Variable type";

    public bool IsStatic => VariableType == EnvironmentVariable.VariableTypes.Static;
    public bool IsMockData => VariableType == EnvironmentVariable.VariableTypes.MockData;
    public bool IsResponseBody => VariableType == EnvironmentVariable.VariableTypes.ResponseBody;
    public bool IsStaticNotSecret => IsStatic && !IsSecret;
    public bool IsStaticAndSecret  => IsStatic && IsSecret;

    /// <summary>
    /// True when the secret lock button should be enabled.
    /// Mock data variables cannot be marked secret.
    /// </summary>
    public bool CanBeSecret => !IsMockData;

    /// <summary>
    /// Tooltip shown on the secret lock button — explains why it is disabled for mock data variables.
    /// </summary>
    public string SecretLockTooltip =>
        IsMockData
            ? "Mock data variables cannot be marked secret"
            : "Mark or unmark as secret — secret values are masked in the UI";

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

            // For concrete Bruno environments, force Static type
            if (IsBrunoConcreteEnvironment)
            {
                VariableType = EnvironmentVariable.VariableTypes.Static;
                return;
            }

            VariableType = value switch
            {
                "Mock Data"           => EnvironmentVariable.VariableTypes.MockData,
                "Response Body Value" => EnvironmentVariable.VariableTypes.ResponseBody,
                _                     => EnvironmentVariable.VariableTypes.Static,
            };
        }
    }

    // ── Preview ───────────────────────────────────────────────────────────────
    // For static variables: expand {{token}} references using resolved environment.
    // For dynamic variables: show the resolved preview value (mock data or response body).

    public string? PreviewValue
    {
        get
        {
            if (IsSecret) return null;

            // For static variables with {{token}} references, substitute them
            if (IsStatic && TokenPattern().IsMatch(Value))
            {
                return VariableSubstitutionService.Substitute(Value, _getResolvedEnv());
            }

            // For dynamic variables (mock data or response body), show the cached preview value
            if ((IsMockData || IsResponseBody) && DynamicPreviewValue != null)
            {
                return DynamicPreviewValue;
            }

            return null;
        }
    }

    public bool HasPreview => !IsSecret && 
        ((IsStatic && TokenPattern().IsMatch(Value)) ||
         ((IsMockData || IsResponseBody) && (DynamicPreviewValue != null || IsDynamicPreviewLoading || IsDynamicPreviewError)));

    /// <summary>True when the resolved preview value TextBlock should be visible (not loading or error).</summary>
    public bool IsPreviewValueVisible => !IsDynamicPreviewLoading && !IsDynamicPreviewError;

    /// <summary>Marks the dynamic preview value as currently loading.</summary>
    internal void MarkDynamicPreviewLoading()
    {
        _dynamicLoadingDelayCts?.Cancel();
        _dynamicLoadingDelayCts?.Dispose();
        // Reset both flags so a stale loading=true from a cancelled refresh doesn't persist.
        IsDynamicPreviewLoading = false;
        IsDynamicPreviewError = false;
        var cts = new CancellationTokenSource();
        _dynamicLoadingDelayCts = cts;
        _ = Task.Delay(200, cts.Token).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (!cts.IsCancellationRequested)
                    IsDynamicPreviewLoading = true;
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>Marks the dynamic preview value as failed to resolve.</summary>
    internal void MarkDynamicPreviewError()
    {
        _dynamicLoadingDelayCts?.Cancel();
        _dynamicLoadingDelayCts?.Dispose();
        _dynamicLoadingDelayCts = null;
        IsDynamicPreviewLoading = false;
        IsDynamicPreviewError = true;
    }

    /// <summary>Clears loading and error states (called when a resolved value arrives).</summary>
    internal void ClearDynamicPreviewState()
    {
        _dynamicLoadingDelayCts?.Cancel();
        _dynamicLoadingDelayCts?.Dispose();
        _dynamicLoadingDelayCts = null;
        IsDynamicPreviewLoading = false;
        IsDynamicPreviewError = false;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{\{[^}]+\}\}")]
    private static partial System.Text.RegularExpressions.Regex TokenPattern();

    public IRelayCommand DeleteCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public EnvironmentVariableItemViewModel(
        Action<EnvironmentVariableItemViewModel> onDelete,
        Action onChanged,
        Func<ResolvedEnvironment> getResolvedEnv,
        Action<string>? onInvalidateDynamicPreviewCache = null,
        Action<string, string>? onUpdateDynamicPreviewCache = null,
        Func<EnvironmentVariable?, Task<EnvironmentVariable?>>? editMockData = null,
        Func<EnvironmentVariable?, Task<EnvironmentVariable?>>? editResponseBody = null)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(getResolvedEnv);
        _onDelete = onDelete;
        _onChanged = onChanged;
        _getResolvedEnv = getResolvedEnv;
        _onInvalidateDynamicPreviewCache = onInvalidateDynamicPreviewCache ?? (_ => { });
        _onUpdateDynamicPreviewCache = onUpdateDynamicPreviewCache ?? ((_, __) => { });
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
        var changed = result.ResponseRequestName          != ResponseRequestName
                   || result.ResponsePath                 != ResponsePath
                   || result.ResponseMatcher             != ResponseMatcher
                   || result.ResponseFrequency            != ResponseFrequency
                   || result.ResponseExpiresAfterSeconds  != ResponseExpiresAfterSeconds;
        ResponseRequestName           = result.ResponseRequestName;
        ResponsePath                  = result.ResponsePath;
        ResponseMatcher               = result.ResponseMatcher;
        ResponseFrequency             = result.ResponseFrequency;
        ResponseExpiresAfterSeconds   = result.ResponseExpiresAfterSeconds;
        if (changed) _onChanged();
    }

    // ── Build / load ──────────────────────────────────────────────────────────

    public EnvironmentVariable BuildModel() => new()
    {
        Name = Name.Trim(),
        Value = Value,
        VariableType = VariableType,
        IsSecret = IsSecret,
        IsForceGlobalOverride = IsForceGlobalOverride,
        MockDataCategory = MockDataCategory,
        MockDataField = MockDataField,
        ResponseRequestName = ResponseRequestName,
        ResponsePath = ResponsePath,
        ResponseMatcher = ResponseMatcher,
        ResponseFrequency = ResponseFrequency,
        ResponseExpiresAfterSeconds = ResponseExpiresAfterSeconds,
    };

    // ── Property change hooks ────────────────────────────────────────────────

    partial void OnNameChanged(string value) => _onChanged();
    
    partial void OnValueChanged(string value) => _onChanged();
    
    partial void OnVariableTypeChanged(string value)
    {
        _onChanged();
        // Invalidate the cached preview when type changes so it will be regenerated
        // on the next refresh cycle
        _onInvalidateDynamicPreviewCache(Name);
    }

    partial void OnIsSecretChanged(bool value)
    {
        _onChanged();
        OnPropertyChanged(nameof(IsStaticNotSecret));
        OnPropertyChanged(nameof(IsStaticAndSecret));
        if (!value) IsValueRevealed = false;
    }

    partial void OnIsForceGlobalOverrideChanged(bool value) => _onChanged();

    partial void OnMockDataCategoryChanged(string? value)
    {
        _onChanged();
        // Regenerate the preview immediately when mock data config changes
        RegenerateMockDataPreview();
    }

    partial void OnMockDataFieldChanged(string? value)
    {
        _onChanged();
        // Regenerate the preview immediately when mock data config changes
        RegenerateMockDataPreview();
    }

    /// <summary>
    /// Regenerates the mock data preview value immediately when the mock data configuration 
    /// (category/field) changes, so the UI shows the new value without navigating away.
    /// Also updates the parent's cache so static variables that reference this mock var
    /// will show the updated value in their previews.
    /// </summary>
    private void RegenerateMockDataPreview()
    {
        if (IsMockData && MockDataCategory is not null && MockDataField is not null)
        {
            var newValue = Callsmith.Core.MockData.MockDataCatalog.Generate(MockDataCategory, MockDataField);
            DynamicPreviewValue = newValue;
            // Update the parent's cache so static vars that reference this mock var get the new value
            _onUpdateDynamicPreviewCache(Name.Trim(), newValue);
        }
        else
        {
            DynamicPreviewValue = null;
        }
    }

    public void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewValue));
        OnPropertyChanged(nameof(HasPreview));
    }
}
