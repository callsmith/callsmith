namespace Callsmith.Core.Models;

/// <summary>
/// A single step within a <see cref="SequenceModel"/>.
/// Each step references a saved request file and may define
/// variable extractions that capture values from the response for use in
/// subsequent steps.
/// </summary>
public sealed class SequenceStep
{
    /// <summary>Stable identifier for this step within the sequence.</summary>
    public required Guid StepId { get; init; }

    /// <summary>
    /// Absolute path to the <c>.callsmith</c> request file that this step executes.
    /// </summary>
    public required string RequestFilePath { get; init; }

    /// <summary>
    /// Display name for this step (typically the request file's name without extension).
    /// Stored in the sequence file so the list remains readable even if the request is moved.
    /// </summary>
    public required string RequestName { get; init; }

    /// <summary>
    /// Variable extractions to perform on the response produced by this step.
    /// Extracted values are injected into the runtime environment as plain string
    /// variables, making them available to subsequent steps via <c>{{variableName}}</c>.
    /// </summary>
    public IReadOnlyList<VariableExtraction> Extractions { get; init; } = [];
}
