using System.Text.RegularExpressions;

namespace Callsmith.Core.Services;

/// <summary>
/// Replaces <c>{{variableName}}</c> tokens in template strings with values
/// from a supplied variable dictionary. Unknown variables are left unchanged.
/// </summary>
public static partial class VariableSubstitutionService
{
    [GeneratedRegex(@"\{\{([^}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Substitutes all <c>{{name}}</c> tokens in <paramref name="template"/>
    /// with the matching value from <paramref name="variables"/>.
    /// Tokens with no matching key are left in place.
    /// </summary>
    /// <param name="template">The string that may contain <c>{{token}}</c> placeholders.</param>
    /// <param name="variables">Case-sensitive variable name → value mapping.</param>
    /// <returns>The resolved string, or <see langword="null"/> when the input is null.</returns>
    public static string? Substitute(string? template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template)) return template;

        return TokenPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return variables.TryGetValue(key, out var value) ? value : match.Value;
        });
    }
}
