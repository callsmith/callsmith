namespace Callsmith.Core.Models;

/// <summary>
/// The overall result of running a <see cref="SequenceModel"/> to completion
/// (or until a step fails).
/// </summary>
public sealed class SequenceRunResult
{
    /// <summary>Ordered results for each step that was executed.</summary>
    public required IReadOnlyList<SequenceStepResult> Steps { get; init; }

    /// <summary>True when all steps completed without error.</summary>
    public required bool IsSuccess { get; init; }

    /// <summary>UTC timestamp at which the sequence run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Wall-clock time elapsed across the entire sequence run.</summary>
    public required TimeSpan TotalElapsed { get; init; }
}
