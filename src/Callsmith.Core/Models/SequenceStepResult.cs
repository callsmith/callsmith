namespace Callsmith.Core.Models;

/// <summary>
/// Captures the outcome of running a single step within a sequence.
/// </summary>
public sealed class SequenceStepResult
{
    /// <summary>Zero-based index of this step within the sequence.</summary>
    public required int StepIndex { get; init; }

    /// <summary>Display name of the request executed by this step.</summary>
    public required string RequestName { get; init; }

    /// <summary>
    /// The HTTP response received, or <see langword="null"/> when the step failed
    /// before a response could be obtained (e.g. network error or missing file).
    /// </summary>
    public ResponseModel? Response { get; init; }

    /// <summary>
    /// Variables extracted from the response and injected into the runtime environment
    /// for subsequent steps. Empty when no extractions are configured or the step failed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtractedVariables { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Human-readable error description when this step did not complete successfully.
    /// <see langword="null"/> on success.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>True when the step completed without error.</summary>
    public bool IsSuccess => Error is null;
}
