using System.Collections.ObjectModel;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the dynamic environment variable configuration dialog.
/// Allows the user to select a collection request, choose a frequency policy,
/// and enter a JSONPath expression to extract a value from the response.
/// </summary>
public sealed partial class DynamicValueConfigViewModel : ObservableObject
{
    private readonly IDynamicVariableEvaluator _evaluator;
    private readonly string _collectionFolderPath;
    private readonly string _environmentFilePath;
    private readonly IReadOnlyList<EnvironmentVariable> _allVariables;
    private readonly IReadOnlyDictionary<string, string> _staticVariables;

    // ─── Available options ────────────────────────────────────────────────────

    /// <summary>All request names available in the current collection (slash-separated paths).</summary>
    public ObservableCollection<string> AvailableRequests { get; } = [];

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
        string environmentFilePath,
        IReadOnlyList<string> availableRequests,
        IReadOnlyList<EnvironmentVariable> allVariables,
        IReadOnlyDictionary<string, string> staticVariables,
        DynamicValueSegment? existing = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(availableRequests);
        ArgumentNullException.ThrowIfNull(allVariables);
        ArgumentNullException.ThrowIfNull(staticVariables);

        _evaluator = evaluator;
        _collectionFolderPath = collectionFolderPath;
        _environmentFilePath = environmentFilePath;
        _allVariables = allVariables;
        _staticVariables = staticVariables;

        foreach (var r in availableRequests)
            AvailableRequests.Add(r);

        _selectedFrequency = FrequencyOptions[0]; // Always

        // Pre-populate from an existing segment when editing
        if (existing is not null)
        {
            _selectedRequest = existing.RequestName;
            _path = existing.Path;
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
            PreviewError = "Select a request and enter a JSONPath expression first.";
            PreviewResult = string.Empty;
            return;
        }

        IsPreviewLoading = true;
        PreviewError = string.Empty;
        PreviewResult = string.Empty;

        try
        {
            var segment = BuildSegment();

            // Resolve all dynamic variables in the environment first so that the target
            // request can use other dynamic values (e.g. an access-token variable) in its
            // headers, auth, or body.  Never/IfExpired hits the cache; Always re-executes.
            IReadOnlyDictionary<string, string> resolvedVars = _staticVariables;
            if (_allVariables.Any(v => v.Segments is { Count: > 0 }))
            {
                resolvedVars = await _evaluator
                    .ResolveAsync(_collectionFolderPath, _environmentFilePath, _allVariables, _staticVariables, ct)
                    .ConfigureAwait(true);
            }

            var result = await _evaluator
                .PreviewAsync(_collectionFolderPath, segment, resolvedVars, ct)
                .ConfigureAwait(true);

            if (result is null)
                PreviewError = "No value extracted. Check the request name or JSONPath expression.";
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
        Frequency = SelectedFrequency.Frequency,
        ExpiresAfterSeconds = IsIfExpiredSelected ? ExpiresAfterSeconds : null,
    };

    // ─── Nested types ────────────────────────────────────────────────────────

    /// <summary>A display item for the frequency ComboBox.</summary>
    public sealed class FrequencyOption(DynamicFrequency frequency, string label)
    {
        public DynamicFrequency Frequency { get; } = frequency;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }
}
