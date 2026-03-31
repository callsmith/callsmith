namespace Callsmith.Core.Helpers;

/// <summary>
/// Pure utility methods for parsing and building URL query strings.
/// No state, no dependencies — safe to call from anywhere.
/// </summary>
public static class QueryStringHelper
{
    /// <summary>
    /// Parses the query parameters out of a URL.
    /// Returns an empty list if the URL has no query string.
    /// Keys and values are URL-decoded. Duplicate keys are preserved in order.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseQueryParams(string url)
    {
        if (string.IsNullOrEmpty(url))
            return [];

        var urlParts = url.Split('?', 2);
        if (urlParts.Length == 1) return [];

        var query = urlParts[1];
        if (string.IsNullOrEmpty(query))
            return [];

        var result = new List<KeyValuePair<string, string>>();
        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq < 0)
                result.Add(new KeyValuePair<string, string>(Uri.UnescapeDataString(segment), string.Empty));
            else
                result.Add(new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(segment[..eq]),
                    Uri.UnescapeDataString(segment[(eq + 1)..])));
        }
        return result;
    }

    /// <summary>
    /// Returns the base URL with the query string stripped.
    /// If the URL has no query string, the original value is returned unchanged.
    /// </summary>
    public static string GetBaseUrl(string url)
    {
        var index = url.IndexOf('?');
        return index >= 0 ? url[..index] : url;
    }

    /// <summary>
    /// Returns the URL with its query string replaced by the supplied key/value pairs.
    /// Any existing query string in <paramref name="url"/> is stripped first.
    /// Keys and values are URL-encoded. If <paramref name="queryParams"/> is empty the
    /// query string is stripped and the bare base URL is returned.
    /// </summary>
    public static string ApplyQueryParams(string url, IEnumerable<KeyValuePair<string, string>> queryParams)
    {
        var pairs = queryParams.ToList();
        var baseUrl = url.Contains('?') ? url[..url.IndexOf('?')] : url;

        if (pairs.Count == 0)
            return baseUrl;

        var qs = string.Join("&", pairs.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        return $"{baseUrl}?{qs}";
    }

    /// <summary>
    /// Returns the URL with the supplied key/value pairs appended after any existing query string.
    /// Existing query parameters are preserved in order.
    /// </summary>
    public static string AppendQueryParams(string url, IEnumerable<KeyValuePair<string, string>> queryParams)
    {
        var appended = queryParams.ToList();
        if (appended.Count == 0)
            return url;

        var merged = ParseQueryParams(url).Concat(appended).ToList();
        return ApplyQueryParams(url, merged);
    }
}
