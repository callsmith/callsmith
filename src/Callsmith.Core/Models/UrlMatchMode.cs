namespace Callsmith.Core.Models;

/// <summary>Controls how a URL pattern is matched.</summary>
public enum UrlMatchMode
{
    /// <summary>The URL must contain the pattern string (default).</summary>
    Contains,

    /// <summary>The URL must start with the pattern string.</summary>
    StartsWith,

    /// <summary>The pattern is treated as a regular expression.</summary>
    Regex,
}
