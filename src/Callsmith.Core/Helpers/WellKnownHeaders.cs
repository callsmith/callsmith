namespace Callsmith.Core.Helpers;

/// <summary>
/// Canonical names for commonly used HTTP headers.
/// Use these constants wherever a header name string is needed to avoid magic strings.
/// </summary>
public static class WellKnownHeaders
{
    /// <summary>The Authorization header.</summary>
    public const string Authorization = "Authorization";

    /// <summary>The Content-Type header.</summary>
    public const string ContentType = "Content-Type";
}
