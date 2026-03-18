using Callsmith.Core.Models;

namespace Callsmith.Core.Import;

/// <summary>
/// A variable imported from an external format that has a composite value made up of
/// static text and dynamic request references.
/// </summary>
public sealed class ImportedDynamicVariable
{
    /// <summary>Variable name (key).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The segments that compose the variable's value in order.
    /// At least one segment will be a <see cref="DynamicValueSegment"/>.
    /// </summary>
    public required IReadOnlyList<ValueSegment> Segments { get; init; }
}
