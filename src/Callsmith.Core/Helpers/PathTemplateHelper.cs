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
    /// Only matches brace-style placeholders (<c>{id}</c>).
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
    /// Extracts distinct placeholder names from the URL path template recognising both
    /// brace syntax (<c>{id}</c>) and colon syntax (<c>:id</c>), in URL order.
    /// Used by Callsmith collections, which support both syntaxes interchangeably.
    /// Query string and fragment are ignored.
    /// </summary>
    public static IReadOnlyList<string> ExtractPathParamNamesBoth(string urlTemplate)
    {
        if (string.IsNullOrWhiteSpace(urlTemplate))
            return [];

        var pathPart = StripQueryAndFragment(urlTemplate);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();

        // Merge matches from both patterns and walk them in URL order.
        var allMatches = PathParamPattern().Matches(pathPart).Cast<Match>()
            .Concat(ColonPathParamPattern().Matches(pathPart).Cast<Match>())
            .OrderBy(m => m.Index);

        foreach (var match in allMatches)
        {
            var name = match.Groups[1].Value;
            if (seen.Add(name))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Replaces placeholders in the URL path using the provided values.
    /// Recognises both brace syntax (<c>{id}</c>) and colon syntax (<c>:id</c>).
    /// Unknown placeholders are left unchanged.
    /// Query string and fragment are preserved as-is.
    /// Values that contain <c>{{</c> (unresolved environment tokens) are substituted
    /// verbatim without URL-encoding so that the tokens remain readable in preview URLs.
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

            // Parity with brace behaviour: leave unresolved {{token}} values un-encoded
            // so they remain readable in preview URLs.
            if (value.Contains("{{", StringComparison.Ordinal))
                return value;

            return Uri.EscapeDataString(value);
        });

        return replacedPath + suffix;
    }

    /// <summary>
    /// Renames a path parameter placeholder in the URL, preserving the syntax form
    /// (brace <c>{name}</c> or colon <c>:name</c>) in which it appears.
    /// Works for pure-brace, pure-colon, and mixed URLs.
    /// Query string and fragment are preserved unchanged.
    /// </summary>
    public static string RenamePathParam(string urlTemplate, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(urlTemplate) || string.IsNullOrEmpty(oldName))
            return urlTemplate;

        var pathPart = StripQueryAndFragment(urlTemplate, out var suffix);

        // Replace brace form using the same single-brace rules as PathParamPattern(),
        // so double-brace env tokens like {{tenant}} are never affected.
        var result = PathParamPattern().Replace(pathPart, match =>
        {
            var key = match.Groups[1].Value;
            return string.Equals(key, oldName, StringComparison.Ordinal)
                ? $"{{{newName}}}"
                : match.Value;
        });

        // Replace colon form at path-segment boundaries (same rules as ColonPathParamPattern)
        // using a MatchEvaluator so replacement text is inserted literally.
        result = ColonPathParamPattern().Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            return string.Equals(key, oldName, StringComparison.Ordinal)
                ? $":{newName}"
                : match.Value;
        });

        return result + suffix;
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
