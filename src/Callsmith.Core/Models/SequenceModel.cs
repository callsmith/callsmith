namespace Callsmith.Core.Models;

/// <summary>
/// A named, ordered list of requests that are executed in sequence.
/// Stored as a <c>.seq.callsmith</c> JSON file in the <c>sequences/</c> sub-folder
/// of a collection. Variable extractions from each step's response are injected
/// into the runtime environment, making them available to subsequent steps.
/// </summary>
public sealed record SequenceModel
{
    /// <summary>Stable unique identifier for this sequence.</summary>
    public required Guid SequenceId { get; init; }

    /// <summary>Absolute path of the sequence file on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>Display name of this sequence.</summary>
    public required string Name { get; init; }

    /// <summary>The ordered list of steps that make up this sequence.</summary>
    public IReadOnlyList<SequenceStep> Steps { get; init; } = [];
}
