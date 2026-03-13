namespace Callsmith.Core.Helpers;

/// <summary>
/// Pure utility methods for parsing and building URL query strings.
/// No state, no dependencies — safe to call from anywhere.
/// </summary>
public static class QueryStringHelper
{
    /// <summary>
    /// Parses the query parameters out of a URL.
    /// Returns an empty dictionary if the URL has no query string or is not a valid absolute URI.
    /// Keys and values are URL-decoded.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseQueryParams(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new Dictionary<string, string>();

        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query))
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq < 0)
                result[Uri.UnescapeDataString(segment)] = string.Empty;
            else
                result[Uri.UnescapeDataString(segment[..eq])] = Uri.UnescapeDataString(segment[(eq + 1)..]);
        }
        return result;
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
}
