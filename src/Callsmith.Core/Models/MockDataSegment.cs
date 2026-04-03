namespace Callsmith.Core.Models;

/// <summary>
/// A mock data segment that generates a realistic fake value using Bogus at evaluation time.
/// The category and field map to entries in <c>MockDataCatalog</c> (e.g. "Internet" / "Email").
/// A fresh value is generated on every evaluation — there is no caching.
/// </summary>
public sealed class MockDataSegment : ValueSegment
{
    /// <summary>The top-level category e.g. "Internet", "Name", "Address".</summary>
    public required string Category { get; init; }

    /// <summary>The specific field within the category e.g. "Email", "First Name".</summary>
    public required string Field { get; init; }
}
