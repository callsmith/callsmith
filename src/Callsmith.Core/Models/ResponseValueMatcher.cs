namespace Callsmith.Core.Models;

/// <summary>
/// Describes how a response-body variable extracts a value from the response payload.
/// </summary>
public enum ResponseValueMatcher
{
    /// <summary>Extract using JSONPath expression syntax.</summary>
    JsonPath,

    /// <summary>Extract using XPath expression syntax.</summary>
    XPath,

    /// <summary>Extract using a regular expression (first match).</summary>
    Regex,
}
