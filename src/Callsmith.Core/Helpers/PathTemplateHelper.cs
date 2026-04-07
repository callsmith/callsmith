using System.Text.RegularExpressions;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Utilities for working with URL path templates that contain placeholders.
/// Supports both Callsmith brace syntax (<c>/users/{id}</c>) and Bruno colon syntax
/// (<c>/users/:id</c>).
/// </summary>
public static partial class PathTemplateHelper
{
    // Match single-brace placeholders like {id}, but do not match {{envVar}}.
    [GeneratedRegex(@"(?<!\{)\{([A-Za-z0-9\._-]+)\}(?!\})", RegexOptions.Compiled)]
    private static partial Regex PathParamPattern();

    // Match colon path params like :userId — but only when preceded by '/' to avoid matching
    // URL scheme (https:), host:port, or auth credentials.
    [GeneratedRegex(@"(?<=/):([A-Za-z][A-Za-z0-9\._-]*)", RegexOptions.Compiled)]
    private static partial Regex ColonPathParamPattern();

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

            if (value.Contains("{{", StringComparison.Ordinal))
                return value;

            return Uri.EscapeDataString(value);
        });

        replacedPath = ColonPathParamPattern().Replace(replacedPath, match =>
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

    // ── Bruno colon-syntax (:variable) helpers ───────────────────────────────

    /// <summary>
    /// Extracts distinct placeholder names from the URL path template using Bruno colon syntax
    /// (e.g. <c>/users/:id/orders/:orderId</c>). Query string and fragment are ignored.
    /// </summary>
    public static IReadOnlyList<string> ExtractPathParamNamesColon(string urlTemplate)
    {
        if (string.IsNullOrWhiteSpace(urlTemplate))
            return [];

        var pathPart = StripQueryAndFragment(urlTemplate);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();

        foreach (Match match in ColonPathParamPattern().Matches(pathPart))
        {
            var name = match.Groups[1].Value;
            if (seen.Add(name))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Replaces Bruno colon placeholders in the URL path using the provided values.
    /// Unknown placeholders are left unchanged. Query string and fragment are preserved.
    /// </summary>
    public static string ApplyPathParamsColon(
        string urlTemplate,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(urlTemplate);
        ArgumentNullException.ThrowIfNull(values);

        if (urlTemplate.Length == 0)
            return urlTemplate;

        var pathPart = StripQueryAndFragment(urlTemplate, out var suffix);

        var replacedPath = ColonPathParamPattern().Replace(pathPart, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
                return match.Value;

            return Uri.EscapeDataString(value);
        });

        return replacedPath + suffix;
    }
}
