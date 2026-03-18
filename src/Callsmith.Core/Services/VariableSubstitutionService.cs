using System.Text.RegularExpressions;
using Callsmith.Core.Helpers;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

/// <summary>
/// Replaces <c>{{variableName}}</c> and <c>{% faker %}</c> tokens in template strings.
/// Unknown variables and unrecognised tags are left unchanged.
/// Variable values may themselves reference other variables; they are resolved
/// transitively up to a fixed depth to prevent infinite loops.
/// </summary>
public static partial class VariableSubstitutionService
{
    /// <summary>Maximum number of expansion passes over the variable map.</summary>
    private const int MaxResolutionDepth = 10;

    [GeneratedRegex(@"\{\{([^}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();

    // Matches {% faker 'Category.Field' %}
    [GeneratedRegex(@"\{%-?\s*faker\s+'([^']+)'\s*-?%\}", RegexOptions.Compiled)]
    private static partial Regex FakerTag();

    /// <summary>
    /// Substitutes all <c>{{name}}</c> tokens in <paramref name="template"/>
    /// with the matching value from <paramref name="variables"/>.
    /// Also evaluates <c>{% faker 'Category.Field' %}</c> mock-data tokens inline.
    /// Variable values that themselves contain <c>{{token}}</c> placeholders are
    /// resolved transitively. Circular references and unknown tokens are left intact.
    /// <para>
    /// <c>{% response ... %}</c> tokens are <em>not</em> evaluated here — they require
    /// async I/O and are handled by <see cref="DynamicVariableEvaluatorService"/> at
    /// environment-resolve time, or by the request-send pipeline for inline field values.
    /// </para>
    /// </summary>
    /// <param name="template">The string that may contain <c>{{token}}</c> placeholders.</param>
    /// <param name="variables">Case-sensitive variable name → value mapping.</param>
    /// <returns>The resolved string, or <see langword="null"/> when the input is null.</returns>
    public static string? Substitute(string? template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var resolved = ResolveVariables(variables);

        var result = TokenPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return resolved.TryGetValue(key, out var value) ? value : match.Value;
        });

        // Evaluate any remaining {% faker %} tags (generated at each call so values differ)
        if (result.Contains("{%", StringComparison.Ordinal))
            result = EvaluateFakerTags(result);

        return result;
    }

    /// <summary>
    /// Evaluates <c>{% faker 'Category.Field' %}</c> tokens in the string,
    /// replacing each with a freshly generated mock value.
    /// </summary>
    private static string EvaluateFakerTags(string input) =>
        FakerTag().Replace(input, match =>
        {
            var key = match.Groups[1].Value;
            var segments = SegmentSerializer.ParseToSegments($"{match.Value}");
            if (segments is [MockDataSegment mockSeg])
                return MockDataCatalog.Generate(mockSeg.Category, mockSeg.Field);

            // Fallback: parse the key directly
            var dotIdx = key.IndexOf('.', StringComparison.Ordinal);
            if (dotIdx > 0)
            {
                var cat = key[..dotIdx];
                var fld = key[(dotIdx + 1)..];
                var generated = MockDataCatalog.Generate(cat, fld);
                if (!string.IsNullOrEmpty(generated)) return generated;
            }

            // Try Bogus-name lookup
            var entry = MockDataCatalog.FindByBogusName(key);
            return entry is not null
                ? MockDataCatalog.Generate(entry.Category, entry.Field)
                : match.Value; // unknown — leave intact
        });

    /// <summary>
    /// Iteratively expands variable values that reference other variables until
    /// the map is stable or <see cref="MaxResolutionDepth"/> passes have run.
    /// Self-references are left unresolved to avoid infinite expansion.
    /// </summary>
    private static Dictionary<string, string> ResolveVariables(IReadOnlyDictionary<string, string> variables)
    {
        var resolved = new Dictionary<string, string>(variables);

        for (var pass = 0; pass < MaxResolutionDepth; pass++)
        {
            // Snapshot the values at the start of this pass so each variable
            // expands against the same generation of values.
            var snapshot = new Dictionary<string, string>(resolved);
            var changed = false;

            foreach (var key in resolved.Keys.ToList())
            {
                var expanded = TokenPattern().Replace(resolved[key], match =>
                {
                    var refKey = match.Groups[1].Value;
                    // Leave self-references in place to prevent infinite expansion.
                    if (refKey == key) return match.Value;
                    return snapshot.TryGetValue(refKey, out var val) ? val : match.Value;
                });

                if (expanded != resolved[key])
                {
                    resolved[key] = expanded;
                    changed = true;
                }
            }

            if (!changed) break;
        }

        return resolved;
    }
}
