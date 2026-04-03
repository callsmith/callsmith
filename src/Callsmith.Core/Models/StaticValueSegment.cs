namespace Callsmith.Core.Models;

/// <summary>A fixed string that contributes its text verbatim to the final variable value.</summary>
public sealed class StaticValueSegment : ValueSegment
{
    /// <summary>The literal text for this segment.</summary>
    public required string Text { get; init; }
}
