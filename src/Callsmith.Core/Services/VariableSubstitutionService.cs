using System.Text.RegularExpressions;
using Callsmith.Core.Helpers;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

/// <summary>
/// Replaces <c>{{variableName}}</c> tokens in template strings, with support for
/// mock-data generator variables (evaluated freshly per token reference) and legacy
/// <c>{% faker %}</c> inline tags for backward compatibility with old imported data.
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

    // Retained for backward compatibility: evaluates legacy {% faker %} tags left inline by old imports.
    [GeneratedRegex(@"\{%-?\s*faker\s+'([^']+)'\s*-?%\}", RegexOptions.Compiled)]
    private static partial Regex FakerTag();

    /// <summary>
    /// Substitutes all <c>{{name}}</c> tokens in <paramref name="template"/> using the
    /// pre-resolved <see cref="ResolvedEnvironment"/>.
    /// </summary>
    public static string? Substitute(string? template, ResolvedEnvironment env) =>
        Substitute(template, env.Variables, env.MockGenerators);

    /// <summary>
    /// Substitutes all <c>{{name}}</c> tokens in <paramref name="template"/>
    /// with the matching value from <paramref name="variables"/>.
    /// </summary>
    public static string? Substitute(
        string? template,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators = null)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var resolved = ResolveVariables(variables);

        var result = TokenPattern().Replace(template, match =>
        {
            var key = NormalizeTokenName(match.Groups[1].Value);
            if (TryGetByTokenName(mockGenerators, key, out var entry))
                return MockDataCatalog.Generate(entry.Category, entry.Field);

            return TryGetByTokenName(resolved, key, out var value)
                ? value
                : match.Value;
        });

        if (result.Contains("{%", StringComparison.Ordinal))
            result = EvaluateFakerTags(result);

        return result;
    }

    /// <summary>
    /// Substitutes all <c>{{name}}</c> tokens in <paramref name="template"/> and records
    /// each substitution in <paramref name="collector"/> as a <see cref="VariableBinding"/>.
    /// </summary>
    public static string? SubstituteCollecting(
        string? template,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlySet<string> secretVariableNames,
        IList<VariableBinding> collector,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators = null)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var resolved = ResolveVariables(variables);

        var result = TokenPattern().Replace(template, match =>
        {
            var tokenText = match.Value;
            var key = NormalizeTokenName(match.Groups[1].Value);

            if (TryGetByTokenName(mockGenerators, key, out var entry))
            {
                var generated = MockDataCatalog.Generate(entry.Category, entry.Field);
                collector.Add(new VariableBinding(tokenText, generated, IsSecret: false));
                return generated;
            }

            if (TryGetByTokenName(resolved, key, out var value))
            {
                var isSecret = secretVariableNames.Contains(key);
                collector.Add(new VariableBinding(tokenText, value, isSecret));
                return value;
            }

            return match.Value;
        });

        if (result.Contains("{%", StringComparison.Ordinal))
            result = EvaluateFakerTags(result);

        return result;
    }

    private static string EvaluateFakerTags(string input) =>
        FakerTag().Replace(input, match =>
        {
            var key = match.Groups[1].Value;
            var segments = SegmentSerializer.ParseToSegments($"{match.Value}");
            if (segments is [MockDataSegment mockSeg])
                return MockDataCatalog.Generate(mockSeg.Category, mockSeg.Field);

            var dotIdx = key.IndexOf('.', StringComparison.Ordinal);
            if (dotIdx > 0)
            {
                var cat = key[..dotIdx];
                var fld = key[(dotIdx + 1)..];
                var generated = MockDataCatalog.Generate(cat, fld);
                if (!string.IsNullOrEmpty(generated)) return generated;
            }

            return match.Value;
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
            var snapshot = new Dictionary<string, string>(resolved);
            var changed = false;

            foreach (var key in resolved.Keys.ToList())
            {
                var expanded = TokenPattern().Replace(resolved[key], match =>
                {
                    var refKey = NormalizeTokenName(match.Groups[1].Value);
                    if (NormalizeTokenName(refKey) == NormalizeTokenName(key))
                        return match.Value;

                    return TryGetByTokenName(snapshot, refKey, out var val)
                        ? val
                        : match.Value;
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

    private static string NormalizeTokenName(string tokenName)
    {
        var trimmed = tokenName.Trim();
        return IsWrappedToken(trimmed)
            ? trimmed[2..^2].Trim()
            : trimmed;
    }

    private static bool IsWrappedToken(string value) =>
        value.Length >= 4
        && value.StartsWith("{{", StringComparison.Ordinal)
        && value.EndsWith("}}", StringComparison.Ordinal);

    private static IEnumerable<string> CandidateKeys(string normalizedKey)
    {
        yield return normalizedKey;
        yield return $"{{{{{normalizedKey}}}}}";
    }

    private static bool TryGetByTokenName<T>(
        IReadOnlyDictionary<string, T>? source,
        string normalizedKey,
        out T value)
    {
        if (source is not null)
        {
            foreach (var candidate in CandidateKeys(normalizedKey))
            {
                if (source.TryGetValue(candidate, out value!))
                    return true;
            }
        }

        value = default!;
        return false;
    }
}