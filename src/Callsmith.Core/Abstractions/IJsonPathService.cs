using System.Text.Json;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Evaluates RFC 9535 JSONPath expressions against JSON documents.
/// Also supports sort extension functions: <c>sort(expr?)</c>, <c>sort_asc(expr?)</c>, <c>sort_desc(expr?)</c>.
/// </summary>
public interface IJsonPathService
{
    /// <summary>
    /// Evaluates a JSONPath expression against the given root element and returns all matching nodes.
    /// Returns an empty list when the expression does not match or when the expression is invalid.
    /// </summary>
    IReadOnlyList<JsonElement> Query(JsonElement root, string expression);

    /// <summary>
    /// Validates a JSONPath expression without evaluating it.
    /// Returns <see langword="false"/> and sets <paramref name="error"/> if the expression is syntactically invalid.
    /// </summary>
    bool TryValidate(string expression, out string error);

    /// <summary>
    /// Evaluates a JSONPath expression and surfaces both syntax and runtime errors.
    /// Runtime errors include sort applied to a non-array, or sort constraint violations.
    /// Returns <see langword="false"/> and sets <paramref name="error"/> on failure.
    /// </summary>
    bool TryQuery(JsonElement root, string expression,
        out IReadOnlyList<JsonElement> results, out string error);
}
