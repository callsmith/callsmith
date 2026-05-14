using System.Collections.ObjectModel;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for a single step within a sequence editor.
/// Holds the request reference, extraction rules, and the run result for this step.
/// </summary>
public sealed partial class SequenceStepViewModel : ObservableObject
{
    private readonly Action<SequenceStepViewModel> _requestRemove;
    private readonly Action<SequenceStepViewModel> _requestMoveUp;
    private readonly Action<SequenceStepViewModel> _requestMoveDown;

    // ─── Identity ────────────────────────────────────────────────────────────

    public Guid StepId { get; } = Guid.NewGuid();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequestPath))]
    private string _requestFilePath = string.Empty;

    [ObservableProperty]
    private string _requestName = string.Empty;

    public bool HasRequestPath => !string.IsNullOrWhiteSpace(RequestFilePath);

    // ─── Extractions ─────────────────────────────────────────────────────────

    public ObservableCollection<VariableExtractionViewModel> Extractions { get; } = [];

    // ─── Run result state ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(ResultStatusColor))]
    [NotifyPropertyChangedFor(nameof(ResultStatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ResultElapsedDisplay))]
    [NotifyPropertyChangedFor(nameof(ResultIsSuccess))]
    [NotifyPropertyChangedFor(nameof(ResultErrorMessage))]
    [NotifyPropertyChangedFor(nameof(ResultExtractedDisplay))]
    private SequenceStepResult? _stepResult;

    public bool HasResult => StepResult is not null;

    public bool ResultIsSuccess => StepResult?.IsSuccess == true;

    public string ResultStatusDisplay => StepResult switch
    {
        null => string.Empty,
        { IsSuccess: false, Error: { } err } => $"Error",
        { Response: { } r } => r.StatusCode.ToString(),
        _ => "—",
    };

    public string ResultStatusColor => StepResult switch
    {
        null => "#555555",
        { IsSuccess: false } => "#c0392b",
        { Response: { StatusCode: >= 200 and < 300 } } => "#27ae60",
        { Response: { StatusCode: >= 300 and < 400 } } => "#f39c12",
        { Response: { StatusCode: >= 400 } } => "#e74c3c",
        _ => "#555555",
    };

    public string ResultElapsedDisplay => StepResult?.Response?.Elapsed is { } e
        ? e.TotalMilliseconds < 1000
            ? $"{e.TotalMilliseconds:F0} ms"
            : $"{e.TotalSeconds:F2} s"
        : string.Empty;

    public string? ResultErrorMessage => StepResult?.Error;

    public string ResultExtractedDisplay
    {
        get
        {
            if (StepResult is null || StepResult.ExtractedVariables.Count == 0)
                return string.Empty;
            return string.Join(", ",
                StepResult.ExtractedVariables.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    public SequenceStepViewModel(
        SequenceStep step,
        Action<SequenceStepViewModel> requestRemove,
        Action<SequenceStepViewModel> requestMoveUp,
        Action<SequenceStepViewModel> requestMoveDown)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(requestRemove);
        ArgumentNullException.ThrowIfNull(requestMoveUp);
        ArgumentNullException.ThrowIfNull(requestMoveDown);

        _requestRemove = requestRemove;
        _requestMoveUp = requestMoveUp;
        _requestMoveDown = requestMoveDown;

        StepId = step.StepId;
        _requestFilePath = step.RequestFilePath;
        _requestName = step.RequestName;

        foreach (var e in step.Extractions)
            Extractions.Add(new VariableExtractionViewModel(e, RemoveExtraction));
    }

    public SequenceStepViewModel(
        Action<SequenceStepViewModel> requestRemove,
        Action<SequenceStepViewModel> requestMoveUp,
        Action<SequenceStepViewModel> requestMoveDown)
    {
        ArgumentNullException.ThrowIfNull(requestRemove);
        ArgumentNullException.ThrowIfNull(requestMoveUp);
        ArgumentNullException.ThrowIfNull(requestMoveDown);

        _requestRemove = requestRemove;
        _requestMoveUp = requestMoveUp;
        _requestMoveDown = requestMoveDown;
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Remove() => _requestRemove(this);

    [RelayCommand]
    private void MoveUp() => _requestMoveUp(this);

    [RelayCommand]
    private void MoveDown() => _requestMoveDown(this);

    [RelayCommand]
    private void AddExtraction() =>
        Extractions.Add(new VariableExtractionViewModel(RemoveExtraction));

    private void RemoveExtraction(VariableExtractionViewModel vm) =>
        Extractions.Remove(vm);

    /// <summary>Exports this VM's state as a domain model step.</summary>
    public SequenceStep ToModel() => new()
    {
        StepId = StepId,
        RequestFilePath = RequestFilePath.Trim(),
        RequestName = RequestName.Trim(),
        Extractions = Extractions
            .Where(e => !string.IsNullOrWhiteSpace(e.VariableName))
            .Select(e => e.ToModel())
            .ToList(),
    };
}
