using System.Text.Json;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Lightweight JSONPath extractor that supports common dot-notation path expressions
/// without requiring an external library. Handles the subset of JSONPath most commonly
/// used in dynamic environment variable configurations.
/// <para>
/// Supported syntax: <c>$</c>, <c>$.field</c>, <c>$.field.nested</c>,
/// <c>$.array[0]</c>, <c>$.field.array[2].sub</c>.
/// </para>
/// </summary>
internal static class JsonPathHelper
{
    /// <summary>
    /// Extracts a string value from a JSON string using a simple JSONPath expression.
    /// Returns <see langword="null"/> when the path does not match any element.
    /// </summary>
    /// <param name="json">The raw JSON string to query.</param>
    /// <param name="path">
    /// A JSONPath expression, e.g. <c>$.token</c>, <c>$.data.access_token</c>,
    /// <c>$.results[0].value</c>.
    /// </param>
    public static string? Extract(string json, string path)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;

            // Normalise: strip the leading '$' and optional '.'
            var normalised = path.Trim();
            if (normalised.StartsWith("$.", StringComparison.Ordinal))
                normalised = normalised[2..];
            else if (normalised.StartsWith("$", StringComparison.Ordinal))
                normalised = normalised[1..];

            // '$' alone means "the root element"
            if (string.IsNullOrEmpty(normalised))
                return JsonElementToString(element);

            foreach (var token in TokenisePath(normalised))
            {
                if (token.PropertyName.Length > 0)
                {
                    if (element.ValueKind != JsonValueKind.Object) return null;
                    if (!element.TryGetProperty(token.PropertyName, out element)) return null;
                }

                if (token.ArrayIndex.HasValue)
                {
                    if (element.ValueKind != JsonValueKind.Array) return null;
                    var idx = token.ArrayIndex.Value;
                    if (idx < 0 || idx >= element.GetArrayLength()) return null;
                    element = element[idx];
                }
            }

            return JsonElementToString(element);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ─── Private helpers ────────────────────────────────────────────────────

    private readonly record struct PathToken(string PropertyName, int? ArrayIndex);

    /// <summary>
    /// Splits a normalised path (no leading <c>$</c>) into tokens.
    /// A path like <c>data.results[0].value</c> yields:
    /// <c>["data"], ["results", 0], ["value"]</c>.
    /// </summary>
    private static IEnumerable<PathToken> TokenisePath(string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (string.IsNullOrEmpty(part)) continue;

            var bracketStart = part.IndexOf('[');
            if (bracketStart < 0)
            {
                // Simple property: "field"
                yield return new PathToken(part, null);
                continue;
            }

            // Property with potential array index: "results[0]"
            var propertyName = part[..bracketStart];
            var bracketEnd = part.IndexOf(']', bracketStart);

            // Emit the property part first (may be empty for bare "[0]" paths)
            if (propertyName.Length > 0)
                yield return new PathToken(propertyName, null);

            // Emit the array index
            if (bracketEnd > bracketStart &&
                int.TryParse(part[(bracketStart + 1)..bracketEnd], out var idx))
            {
                yield return new PathToken(string.Empty, idx);
            }
        }
    }

    private static string? JsonElementToString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
}
