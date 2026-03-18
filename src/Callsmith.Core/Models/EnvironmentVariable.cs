namespace Callsmith.Core.Models;

/// <summary>
/// A single variable within an environment.
/// For Phase 6, only static and secret types are supported.
/// Script and chained variable types are reserved for a future phase.
/// </summary>
public sealed class EnvironmentVariable
{
    /// <summary>The variable name used in <c>{{variableName}}</c> substitution.</summary>
    public required string Name { get; init; }

    /// <summary>The raw value of the variable.</summary>
    public required string Value { get; init; }

    /// <summary>The variable type — determines how the value is resolved at send time.</summary>
    public string VariableType { get; init; } = VariableTypes.Static;

    /// <summary>
    /// Whether this variable holds a secret value (token, password, API key, etc.).
    /// Secret variables are masked in the UI and never written to request history in plaintext.
    /// </summary>
    public bool IsSecret { get; init; }

    /// <summary>
    /// When non-null and non-empty, the variable value is composed from these segments
    /// (a mix of <see cref="StaticValueSegment"/> and <see cref="DynamicValueSegment"/>).
    /// The <see cref="Value"/> field then holds the last cached/resolved result.
    /// When null or empty, the variable is purely static and <see cref="Value"/> is used directly.
    /// </summary>
    public IReadOnlyList<ValueSegment>? Segments { get; init; }

    /// <summary>Well-known variable type constants.</summary>
    public static class VariableTypes
    {
        /// <summary>Plain string value — used directly in substitution.</summary>
        public const string Static = "static";

        /// <summary>Reserved: JavaScript expression evaluated at request send time.</summary>
        public const string Script = "script";

        /// <summary>Reserved: Extracted from the response of another named request.</summary>
        public const string Chained = "chained";

        /// <summary>
        /// Value composed from static and dynamic segments. The variable has a
        /// non-empty <see cref="EnvironmentVariable.Segments"/> list.
        /// </summary>
        public const string Dynamic = "dynamic";
    }
}
