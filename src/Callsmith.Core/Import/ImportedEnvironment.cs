namespace Callsmith.Core.Import;

/// <summary>
/// A named environment (set of key/value variables) extracted from an external
/// collection format (Insomnia, Postman, etc.) during import. Format-agnostic.
/// </summary>
public sealed class ImportedEnvironment
{
    /// <summary>Display name of the environment (e.g. "Dev", "Staging", "Production").</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Variable key/value pairs in this environment.
    /// Script values (e.g. Insomnia <c>{% response … %}</c> expressions) are imported
    /// separately as <see cref="DynamicVariables"/>; only static string values appear here.
    /// </summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Variables whose value is composed of static text and dynamic request references.
    /// These originate from formats like Insomnia's <c>{% response … %}</c> syntax.
    /// </summary>
    public IReadOnlyList<ImportedDynamicVariable> DynamicVariables { get; init; } = [];

    /// <summary>
    /// Optional display color hint from the source tool (hex format, e.g. <c>"#4ec9b0"</c>).
    /// Null means no color is carried over.
    /// </summary>
    public string? Color { get; init; }
}
