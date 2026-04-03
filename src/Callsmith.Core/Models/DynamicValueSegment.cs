namespace Callsmith.Core.Models;

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
    /// Expression used to extract the desired value from the response body.
    /// Interpreted by <see cref="Matcher"/>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Matcher that interprets <see cref="Path"/> (JSONPath, XPath, or Regex).
    /// </summary>
    public ResponseValueMatcher Matcher { get; init; } = ResponseValueMatcher.JsonPath;

    /// <summary>How often the linked request should be re-executed.</summary>
    public DynamicFrequency Frequency { get; init; }

    /// <summary>
    /// Cache lifetime in seconds when <see cref="Frequency"/> is
    /// <see cref="DynamicFrequency.IfExpired"/>. Defaults to 900 (15 minutes).
    /// </summary>
    public int? ExpiresAfterSeconds { get; init; }
}
