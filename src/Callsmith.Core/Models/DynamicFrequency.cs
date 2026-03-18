namespace Callsmith.Core.Models;

/// <summary>
/// Controls how often a dynamic environment variable's linked request is re-executed.
/// </summary>
public enum DynamicFrequency
{
    /// <summary>Always re-executes the linked request before using the value.</summary>
    Always,

    /// <summary>Re-executes only when the cached value has expired past <c>ExpiresAfterSeconds</c>.</summary>
    IfExpired,

    /// <summary>Executes once on first use; subsequent uses always return the cached result.</summary>
    Never,
}
