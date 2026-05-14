namespace Callsmith.Core.Models;

/// <summary>
/// Describes how to extract a value from a step's response and expose it as an
/// environment variable for subsequent steps.
/// </summary>
public sealed class VariableExtraction
{
    /// <summary>
    /// The name under which the extracted value is stored in the runtime environment.
    /// Subsequent steps can reference it as <c>{{variableName}}</c>.
    /// </summary>
    public required string VariableName { get; init; }

    /// <summary>Where within the response to extract the value from.</summary>
    public required VariableExtractionSource Source { get; init; }

    /// <summary>
    /// The extraction expression whose interpretation depends on <see cref="Source"/>:
    /// <list type="bullet">
    ///   <item><description><see cref="VariableExtractionSource.ResponseBody"/> — a JSONPath expression (e.g. <c>$.token</c>).</description></item>
    ///   <item><description><see cref="VariableExtractionSource.ResponseHeader"/> — the exact header name (e.g. <c>Location</c>).</description></item>
    /// </list>
    /// </summary>
    public required string Expression { get; init; }
}

/// <summary>
/// Specifies where in the HTTP response a variable extraction reads its value from.
/// </summary>
public enum VariableExtractionSource
{
    /// <summary>Extract from the JSON response body using a JSONPath expression.</summary>
    ResponseBody,

    /// <summary>Extract from a response header value by header name.</summary>
    ResponseHeader,
}
