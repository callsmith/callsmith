using System.Text.Json.Serialization;

namespace Callsmith.Core.Models;

/// <summary>
/// One segment of a composite environment variable value.
/// A variable value may be a mix of literal text (<see cref="StaticValueSegment"/>)
/// and dynamic lookup references (<see cref="DynamicValueSegment"/>).
/// For example: <c>"AccessToken "</c> (static) + <c>{requestName=…, path=…}</c> (dynamic).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StaticValueSegment), "static")]
[JsonDerivedType(typeof(DynamicValueSegment), "dynamic")]
[JsonDerivedType(typeof(MockDataSegment), "mock")]
public abstract class ValueSegment { }

/// <summary>A fixed string that contributes its text verbatim to the final variable value.</summary>
public sealed class StaticValueSegment : ValueSegment
{
    /// <summary>The literal text for this segment.</summary>
    public required string Text { get; init; }
}

/// <summary>
/// A dynamic reference that is resolved at evaluation time by executing a collection
/// request and extracting a value from its response.
/// </summary>
public sealed class DynamicValueSegment : ValueSegment
{
    /// <summary>
    /// Collection-relative request path used to identify which request to execute.
    /// For nested requests use forward-slash notation: <c>"Auth/Get Token"</c>.
    /// </summary>
    public required string RequestName { get; init; }

    /// <summary>
    /// JSONPath expression used to extract the desired value from the response body.
    /// Example: <c>$.access_token</c>, <c>$.data.token</c>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>How often the linked request should be re-executed.</summary>
    public DynamicFrequency Frequency { get; init; }

    /// <summary>
    /// Cache lifetime in seconds when <see cref="Frequency"/> is
    /// <see cref="DynamicFrequency.IfExpired"/>. Defaults to 900 (15 minutes).
    /// </summary>
    public int? ExpiresAfterSeconds { get; init; }
}

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
