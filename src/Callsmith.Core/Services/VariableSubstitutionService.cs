using System.Text.RegularExpressions;

namespace Callsmith.Core.Services;

/// <summary>
/// Replaces <c>{{variableName}}</c> tokens in template strings with values
/// from a supplied variable dictionary. Unknown variables are left unchanged.
/// Variable values may themselves reference other variables; they are resolved
/// transitively up to a fixed depth to prevent infinite loops.
/// </summary>
public static partial class VariableSubstitutionService
{
    /// <summary>Maximum number of expansion passes over the variable map.</summary>
    private const int MaxResolutionDepth = 10;

    [GeneratedRegex(@"\{\{([^}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Substitutes all <c>{{name}}</c> tokens in <paramref name="template"/>
    /// with the matching value from <paramref name="variables"/>.
    /// Variable values that themselves contain <c>{{token}}</c> placeholders are
    /// resolved transitively. Circular references and unknown tokens are left intact.
    /// </summary>
    /// <param name="template">The string that may contain <c>{{token}}</c> placeholders.</param>
    /// <param name="variables">Case-sensitive variable name → value mapping.</param>
    /// <returns>The resolved string, or <see langword="null"/> when the input is null.</returns>
    public static string? Substitute(string? template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var resolved = ResolveVariables(variables);

        return TokenPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return resolved.TryGetValue(key, out var value) ? value : match.Value;
        });
    }

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
