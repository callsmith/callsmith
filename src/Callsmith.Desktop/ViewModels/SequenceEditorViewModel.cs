using System.Collections.ObjectModel;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// A selectable request entry shown in the "add step" picker.
/// </summary>
public sealed record AvailableRequest
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    /// <summary>Collection-relative display path (e.g. "auth/Login").</summary>
    public required string DisplayPath { get; init; }
}

/// <summary>
/// Drives the sequence editor: editing steps, configuring extractions, and running the sequence.
/// </summary>
public sealed partial class SequenceEditorViewModel : ObservableObject
{
    private readonly ISequenceService _sequenceService;
    private readonly ISequenceRunnerService _runnerService;
    private readonly ILogger<SequenceEditorViewModel> _logger;
    private CancellationTokenSource? _runCts;

    // ─── Sequence identity ────────────────────────────────────────────────────

    public Guid SequenceId { get; private set; }
    public string FilePath { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _sequenceName = string.Empty;

    private string _savedName = string.Empty;
    public bool IsDirty => SequenceName != _savedName || _stepsChangedSinceLastSave;
    private bool _stepsChangedSinceLastSave;

    // ─── Step list ────────────────────────────────────────────────────────────

    public ObservableCollection<SequenceStepViewModel> Steps { get; } = [];

    // ─── Request picker ───────────────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<AvailableRequest> _availableRequests = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedRequestAsStepCommand))]
    private AvailableRequest? _selectedAvailableRequest;

    // ─── Run state ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunFailed))]
    private bool _hasRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunFailed))]
    private bool _lastRunSuccess;

    [ObservableProperty]
    private string _runStatusMessage = string.Empty;

    [ObservableProperty]
    private string _runDurationDisplay = string.Empty;

    public bool CanRun => !IsRunning && Steps.Count > 0;

    public bool HasRunFailed => HasRun && !LastRunSuccess;

    // ─── Environment (set by SequencesViewModel when env changes) ────────────

    private EnvironmentModel _globalEnvironment = new()
    {
        FilePath = string.Empty,
        EnvironmentId = Guid.NewGuid(),
        Name = "Global",
        Variables = [],
    };
    private EnvironmentModel? _activeEnvironment;
    private string _collectionRootPath = string.Empty;

    public SequenceEditorViewModel(
        ISequenceService sequenceService,
        ISequenceRunnerService runnerService,
        ILogger<SequenceEditorViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(sequenceService);
        ArgumentNullException.ThrowIfNull(runnerService);
        ArgumentNullException.ThrowIfNull(logger);
        _sequenceService = sequenceService;
        _runnerService = runnerService;
        _logger = logger;
    }

    // ─── Load / Save ──────────────────────────────────────────────────────────

    /// <summary>Loads a sequence into the editor.</summary>
    public void LoadSequence(SequenceModel sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        SequenceId = sequence.SequenceId;
        FilePath = sequence.FilePath;
        SequenceName = sequence.Name;
        _savedName = sequence.Name;
        _stepsChangedSinceLastSave = false;

        Steps.Clear();
        foreach (var step in sequence.Steps)
            Steps.Add(CreateStepViewModel(step));

        HasRun = false;
        RunStatusMessage = string.Empty;
        RunDurationDisplay = string.Empty;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanRun));
    }

    /// <summary>Updates the environment context used during sequence runs.</summary>
    public void UpdateEnvironment(
        EnvironmentModel globalEnv,
        EnvironmentModel? activeEnv,
        string collectionRootPath)
    {
        _globalEnvironment = globalEnv;
        _activeEnvironment = activeEnv;
        _collectionRootPath = collectionRootPath;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SequenceName)) return;
        try
        {
            var updated = BuildModel();
            await _sequenceService.SaveSequenceAsync(updated, ct).ConfigureAwait(false);
            _savedName = SequenceName;
            _stepsChangedSinceLastSave = false;
            OnPropertyChanged(nameof(IsDirty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save sequence '{Name}'", SequenceName);
        }
    }

    // ─── Step management ──────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAddSelectedRequest))]
    private void AddSelectedRequestAsStep()
    {
        if (SelectedAvailableRequest is not { } req) return;

        var step = new SequenceStep
        {
            StepId = Guid.NewGuid(),
            RequestFilePath = req.FilePath,
            RequestName = req.Name,
            Extractions = [],
        };
        Steps.Add(CreateStepViewModel(step));
        MarkStepsChanged();
    }

    private bool CanAddSelectedRequest() => SelectedAvailableRequest is not null;

    private void RemoveStep(SequenceStepViewModel vm)
    {
        Steps.Remove(vm);
        MarkStepsChanged();
    }

    private void MoveStepUp(SequenceStepViewModel vm)
    {
        var idx = Steps.IndexOf(vm);
        if (idx <= 0) return;
        Steps.Move(idx, idx - 1);
        MarkStepsChanged();
    }

    private void MoveStepDown(SequenceStepViewModel vm)
    {
        var idx = Steps.IndexOf(vm);
        if (idx < 0 || idx >= Steps.Count - 1) return;
        Steps.Move(idx, idx + 1);
        MarkStepsChanged();
    }

    /// <summary>
    /// Reorders <paramref name="step"/> to <paramref name="destinationIndex"/> within
    /// <see cref="Steps"/>. Intended for drag-and-drop interactions.
    /// </summary>
    public void MoveStep(SequenceStepViewModel step, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(step);

        var currentIndex = Steps.IndexOf(step);
        if (currentIndex < 0) return;

        // Clamp destination into valid range and ignore no-op moves.
        destinationIndex = Math.Clamp(destinationIndex, 0, Steps.Count - 1);
        if (destinationIndex == currentIndex) return;

        Steps.Move(currentIndex, destinationIndex);
        MarkStepsChanged();
    }

    private void MarkStepsChanged()
    {
        _stepsChangedSinceLastSave = true;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
    }

    // ─── Run ─────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync(CancellationToken ct)
    {
        if (IsRunning) return;

        IsRunning = true;
        HasRun = false;
        RunStatusMessage = "Running…";
        RunDurationDisplay = string.Empty;

        // Clear previous step results.
        foreach (var step in Steps)
            step.StepResult = null;

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var sequence = BuildModel();
            var progress = new Progress<SequenceStepResult>(OnStepProgress);

            var result = await _runnerService.RunAsync(
                sequence,
                _globalEnvironment,
                _activeEnvironment,
                _collectionRootPath,
                progress,
                _runCts.Token)
                .ConfigureAwait(false);

            HasRun = true;
            LastRunSuccess = result.IsSuccess;
            RunDurationDisplay = result.TotalElapsed.TotalMilliseconds < 1000
                ? $"{result.TotalElapsed.TotalMilliseconds:F0} ms"
                : $"{result.TotalElapsed.TotalSeconds:F2} s";

            RunStatusMessage = result.IsSuccess
                ? $"All {result.Steps.Count} step{(result.Steps.Count == 1 ? "" : "s")} passed"
                : $"Failed at step {result.Steps.ToList().IndexOf(result.Steps.First(s => !s.IsSuccess)) + 1}";
        }
        catch (OperationCanceledException)
        {
            RunStatusMessage = "Run cancelled.";
        }
        catch (Exception ex)
        {
            RunStatusMessage = $"Run failed: {ex.Message}";
            _logger.LogError(ex, "Sequence run failed");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void CancelRun()
    {
        _runCts?.Cancel();
    }

    private void OnStepProgress(SequenceStepResult result)
    {
        if (result.StepIndex < Steps.Count)
            Steps[result.StepIndex].StepResult = result;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private SequenceStepViewModel CreateStepViewModel(SequenceStep step) =>
        new(step, RemoveStep, MoveStepUp, MoveStepDown);

    private SequenceModel BuildModel() => new()
    {
        SequenceId = SequenceId,
        FilePath = FilePath,
        Name = SequenceName.Trim(),
        Steps = Steps.Select(s => s.ToModel()).ToList(),
    };
}
