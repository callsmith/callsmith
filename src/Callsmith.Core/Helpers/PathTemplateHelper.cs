using System.Text.RegularExpressions;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Utilities for working with URL path templates that contain placeholders,
/// for example: <c>/users/{id}/orders/{orderId}</c>.
/// </summary>
public static partial class PathTemplateHelper
{
    // Match single-brace placeholders like {id}, but do not match {{envVar}}.
    [GeneratedRegex(@"(?<!\{)\{([A-Za-z0-9_-]+)\}(?!\})", RegexOptions.Compiled)]
    private static partial Regex PathParamPattern();

    /// <summary>
    /// Extracts distinct placeholder names from the URL path template in first-seen order.
    /// Query string and fragment are ignored.
    /// </summary>
    public static IReadOnlyList<string> ExtractPathParamNames(string urlTemplate)
    {
        if (string.IsNullOrWhiteSpace(urlTemplate))
            return [];

        var pathPart = StripQueryAndFragment(urlTemplate);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();

        foreach (Match match in PathParamPattern().Matches(pathPart))
        {
            var name = match.Groups[1].Value;
            if (seen.Add(name))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Replaces placeholders in the URL path using the provided values.
    /// Unknown placeholders are left unchanged.
    /// Query string and fragment are preserved as-is.
    /// </summary>
    public static string ApplyPathParams(
        string urlTemplate,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(urlTemplate);
        ArgumentNullException.ThrowIfNull(values);

        if (urlTemplate.Length == 0)
            return urlTemplate;

        var pathPart = StripQueryAndFragment(urlTemplate, out var suffix);

        var replacedPath = PathParamPattern().Replace(pathPart, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
                return match.Value;

            return Uri.EscapeDataString(value);
        });

        return replacedPath + suffix;
    }

    private static string StripQueryAndFragment(string value) =>
        StripQueryAndFragment(value, out _);

    private static string StripQueryAndFragment(string value, out string suffix)
    {
        var queryIndex = value.IndexOf('?');
        var fragmentIndex = value.IndexOf('#');

        var splitAt = queryIndex switch
        {
            >= 0 when fragmentIndex >= 0 => Math.Min(queryIndex, fragmentIndex),
            >= 0 => queryIndex,
            _ when fragmentIndex >= 0 => fragmentIndex,
            _ => -1,
        };

        if (splitAt < 0)
        {
            suffix = string.Empty;
            return value;
        }

        suffix = value[splitAt..];
        return value[..splitAt];
    }
}
