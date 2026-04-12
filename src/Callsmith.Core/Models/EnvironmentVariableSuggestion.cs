namespace Callsmith.Core.Models;

/// <summary>
/// Name/value suggestion item used for environment-variable completions.
/// </summary>
public sealed record EnvironmentVariableSuggestion(string Name, string Value);
