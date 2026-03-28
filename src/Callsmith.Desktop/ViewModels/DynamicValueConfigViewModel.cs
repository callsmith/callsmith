using System.Collections.ObjectModel;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the dynamic environment variable configuration dialog.
/// Allows the user to select a collection request, choose a frequency policy,
/// and enter an extraction expression (JSONPath, XPath, or Regex) to extract a value from the response.
/// </summary>
public sealed partial class DynamicValueConfigViewModel : ObservableObject
{
    private readonly IDynamicVariableEvaluator _evaluator;
    private readonly string _collectionFolderPath;
    private readonly string _environmentCacheNamespace;
    private readonly IReadOnlyList<EnvironmentVariable> _allVariables;
    private readonly IReadOnlyDictionary<string, string> _staticVariables;
    private readonly IReadOnlyList<EnvironmentVariable> _globalVariables;
    private readonly string _globalEnvironmentCacheNamespace;

    // ─── Available options ────────────────────────────────────────────────────

    /// <summary>All request names available in the current collection (slash-separated paths).</summary>
    public ObservableCollection<string> AvailableRequests { get; } = [];

    /// <summary>Matcher options shown in the dropdown.</summary>
    public IReadOnlyList<MatcherOption> MatcherOptions { get; } =
    [
        new(ResponseValueMatcher.JsonPath, "JSONPath"),
        new(ResponseValueMatcher.XPath, "XPath (HTML compatible)"),
        new(ResponseValueMatcher.Regex, "Regex"),
    ];

    /// <summary>Frequency options shown in the dropdown.</summary>
    public IReadOnlyList<FrequencyOption> FrequencyOptions { get; } =
    [
        new(DynamicFrequency.Always,    "Always — run before every use"),
        new(DynamicFrequency.IfExpired, "If Expired — run once, then re-run after lifetime"),
        new(DynamicFrequency.Never,     "Never — run once, then cache forever"),
    ];

    // ─── Bound fields ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIfExpiredSelected))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _selectedRequest = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIfExpiredSelected))]
    private FrequencyOption _selectedFrequency;

    [ObservableProperty]
    private int _expiresAfterSeconds = 900;

    [ObservableProperty]
    private MatcherOption _selectedMatcher;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _previewResult = string.Empty;

    [ObservableProperty]
    private string _previewError = string.Empty;

    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>True when the "If Expired" frequency is selected, showing the lifetime input.</summary>
    public bool IsIfExpiredSelected =>
        SelectedFrequency.Frequency == DynamicFrequency.IfExpired;

    // ─── Result ───────────────────────────────────────────────────────────────

    /// <summary>Set to true when the user confirms. The caller reads <see cref="ResultSegment"/>.</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>The configured segment, available after the user confirms.</summary>
    public DynamicValueSegment? ResultSegment { get; private set; }

    // ─── Close signal ─────────────────────────────────────────────────────────

    /// <summary>Raised when the dialog should close (either confirmed or cancelled).</summary>
    public event EventHandler? CloseRequested;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public DynamicValueConfigViewModel(
        IDynamicVariableEvaluator evaluator,
        string collectionFolderPath,
        string environmentCacheNamespace,
        IReadOnlyList<string> availableRequests,
        IReadOnlyList<EnvironmentVariable> allVariables,
        IReadOnlyDictionary<string, string> staticVariables,
        DynamicValueSegment? existing = null,
        IReadOnlyList<EnvironmentVariable>? globalVariables = null,
        string? globalEnvironmentCacheNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(availableRequests);
        ArgumentNullException.ThrowIfNull(allVariables);
        ArgumentNullException.ThrowIfNull(staticVariables);

        _evaluator = evaluator;
        _collectionFolderPath = collectionFolderPath;
        _environmentCacheNamespace = environmentCacheNamespace;
        _allVariables = allVariables;
        _staticVariables = staticVariables;
        _globalVariables = globalVariables ?? [];
        _globalEnvironmentCacheNamespace = globalEnvironmentCacheNamespace ?? string.Empty;

        foreach (var r in availableRequests)
            AvailableRequests.Add(r);

        _selectedMatcher = MatcherOptions[0]; // JSONPath
        _selectedFrequency = FrequencyOptions[0]; // Always

        // Pre-populate from an existing segment when editing
        if (existing is not null)
        {
            _selectedRequest = existing.RequestName;
            _path = existing.Path;
            _selectedMatcher = MatcherOptions.FirstOrDefault(
                o => o.Matcher == existing.Matcher) ?? MatcherOptions[0];
            _selectedFrequency = FrequencyOptions.FirstOrDefault(
                o => o.Frequency == existing.Frequency) ?? FrequencyOptions[0];
            _expiresAfterSeconds = existing.ExpiresAfterSeconds ?? 900;
        }
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Runs the referenced request and shows the extracted value as a preview.</summary>
    [RelayCommand]
    private async Task PreviewAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SelectedRequest) || string.IsNullOrWhiteSpace(Path))
        {
            PreviewError = "Select a request and enter an extraction expression first.";
            PreviewResult = string.Empty;
            return;
        }

        IsPreviewLoading = true;
        PreviewError = string.Empty;
        PreviewResult = string.Empty;

        try
        {
            var segment = BuildSegment();

            // Resolve all dynamic variables in the environment first.
            IReadOnlyDictionary<string, string> baseStaticVars = _staticVariables;
            // Phase 1: pre-resolve global dynamic vars (e.g. `token`) so their values are in
            // the dict when the active env's dynamic var calls its request.
            // Uses the same env-scoped cache namespace as send time to avoid redundant HTTP calls.
            IReadOnlyDictionary<string, string> resolvedVars = baseStaticVars;
            if (_globalVariables.Any(v =>
                v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody
                || v.VariableType == EnvironmentVariable.VariableTypes.Dynamic))
            {
                var globalCacheNamespace = !string.IsNullOrEmpty(_globalEnvironmentCacheNamespace)
                    ? $"{_globalEnvironmentCacheNamespace}[env:{_environmentCacheNamespace}]"
                    : _environmentCacheNamespace;
                var globalResolved = await _evaluator
                    .ResolveAsync(_collectionFolderPath, globalCacheNamespace, _globalVariables, baseStaticVars, ct)
                    .ConfigureAwait(true);
                var merged = new Dictionary<string, string>(baseStaticVars, StringComparer.Ordinal);
                foreach (var kv in globalResolved.Variables)
                    merged[kv.Key] = kv.Value;
                resolvedVars = merged;
            }

            // Phase 2: resolve the active env's own dynamic variables with the now-complete var set.
            if (_allVariables.Any(v =>
                v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody
                || v.VariableType == EnvironmentVariable.VariableTypes.Dynamic))
            {
                var resolved = await _evaluator
                    .ResolveAsync(_collectionFolderPath, _environmentCacheNamespace, _allVariables, resolvedVars, ct)
                    .ConfigureAwait(true);
                resolvedVars = resolved.Variables;
            }

            // Build a temporary response-body variable to preview via the evaluator.
            var previewVar = new EnvironmentVariable
            {
                Name = "__preview__",
                Value = string.Empty,
                VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                ResponseRequestName = segment.RequestName,
                ResponsePath = segment.Path,
                ResponseMatcher = segment.Matcher,
                ResponseFrequency = segment.Frequency,
                ResponseExpiresAfterSeconds = segment.ExpiresAfterSeconds,
            };

            var result = await _evaluator
                .PreviewResponseBodyAsync(_collectionFolderPath, previewVar, resolvedVars, ct)
                .ConfigureAwait(true);

            if (result is null)
                PreviewError = $"No value extracted. Check the request name or {SelectedMatcher.Label} expression.";
            else
                PreviewResult = result;
        }
        catch (OperationCanceledException)
        {
            PreviewError = "Preview cancelled.";
        }
        catch (Exception ex)
        {
            PreviewError = $"Preview failed: {ex.Message}";
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    /// <summary>Validates input and confirms the segment configuration.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        ResultSegment = BuildSegment();
        IsConfirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels without producing a result.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private bool CanConfirm =>
        !string.IsNullOrWhiteSpace(SelectedRequest) &&
        !string.IsNullOrWhiteSpace(Path);

    private DynamicValueSegment BuildSegment() => new()
    {
        RequestName = SelectedRequest.Trim(),
        Path = Path.Trim(),
        Matcher = SelectedMatcher.Matcher,
        Frequency = SelectedFrequency.Frequency,
        ExpiresAfterSeconds = IsIfExpiredSelected ? ExpiresAfterSeconds : null,
    };

    // ─── Nested types ────────────────────────────────────────────────────────

    /// <summary>A display item for the matcher ComboBox.</summary>
    public sealed class MatcherOption(ResponseValueMatcher matcher, string label)
    {
        public ResponseValueMatcher Matcher { get; } = matcher;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }

    /// <summary>A display item for the frequency ComboBox.</summary>
    public sealed class FrequencyOption(DynamicFrequency frequency, string label)
    {
        public DynamicFrequency Frequency { get; } = frequency;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }
}
