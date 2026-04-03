using System.Text;
using System.Text.RegularExpressions;
using Callsmith.Core.Models;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Describes which API-key auth fields should be masked in the generated cURL output.
/// </summary>
/// <param name="ApiKeyHeaderName">
/// Resolved header name for API-key-in-header auth (e.g. "X-API-Key"). Null when not applicable.
/// </param>
/// <param name="ApiKeyQueryParamName">
/// Resolved query-parameter name for API-key-in-query auth (e.g. "api_key"). Null when not applicable.
/// </param>
public sealed record CurlAuthMaskInfo(
    string? ApiKeyHeaderName,
    string? ApiKeyQueryParamName);

/// <summary>
/// Builds a shell-ready cURL command string from a resolved <see cref="RequestModel"/>.
/// </summary>
public static class CurlCommandBuilder
{
    /// <summary>
    /// Builds a formatted multi-line cURL command for the given request.
    /// </summary>
    /// <param name="request">A fully-resolved request (env vars already substituted).</param>
    /// <param name="maskAuthentication">
    /// When true, auth credentials are replaced with placeholders
    /// (<c>&lt;token&gt;</c> for Authorization headers, <c>&lt;key&gt;</c> for API keys).
    /// </param>
    /// <param name="authMaskInfo">
    /// Additional hints for masking API key auth. Only needed when
    /// <paramref name="maskAuthentication"/> is true and the request uses API key auth.
    /// </param>
    public static string Build(
        RequestModel request,
        bool maskAuthentication = false,
        CurlAuthMaskInfo? authMaskInfo = null)
    {
        // Method — curl defaults to GET, HEAD uses -I. All others need -X.
        var method = request.Method.Method.ToUpperInvariant();
        var methodFlag = method switch
        {
            "GET"  => null,
            "HEAD" => "-I",
            _      => $"-X {method}",
        };

        var sb = new StringBuilder();
        sb.Append("curl");

        if (methodFlag is not null)
            sb.Append($" {methodFlag}");

        // URL — mask API-key query param value if needed
        var url = maskAuthentication && authMaskInfo?.ApiKeyQueryParamName is { } qpName
            ? MaskQueryParamValue(request.Url, qpName)
            : request.Url;

        sb.Append($" \\\n  \"{EscapeUrl(url)}\"");

        // Request headers
        foreach (var (key, value) in request.Headers)
        {
            var displayValue = maskAuthentication ? MaskHeaderValue(key, value, authMaskInfo) : value;
            sb.Append($" \\\n  -H \"{EscapeHeaderValue(key)}: {EscapeHeaderValue(displayValue)}\"");
        }

        // Content-Type injection
        if (request.ContentType is not null &&
            !request.Headers.Keys.Any(k =>
                k.Equals(WellKnownHeaders.ContentType, StringComparison.OrdinalIgnoreCase)))
        {
            sb.Append($" \\\n  -H \"Content-Type: {EscapeHeaderValue(request.ContentType)}\"");
        }

        // Body
        if (!string.IsNullOrEmpty(request.Body))
            sb.Append($" \\\n  --data-raw {QuoteBody(request.Body)}");

        return sb.ToString();
    }

    // Mask the value of a named Authorization header, keeping the scheme.
    // e.g. "Bearer abc123" → "Bearer <token>", "SSWS xyz" → "SSWS <token>"
    private static string MaskHeaderValue(string key, string value, CurlAuthMaskInfo? info)
    {
        if (key.Equals(WellKnownHeaders.Authorization, StringComparison.OrdinalIgnoreCase))
        {
            var spaceIdx = value.IndexOf(' ');
            return spaceIdx < 0 ? "<token>" : $"{value[..spaceIdx]} <token>";
        }

        if (info?.ApiKeyHeaderName is { } headerName &&
            key.Equals(headerName, StringComparison.OrdinalIgnoreCase))
            return "<key>";

        return value;
    }

    // Replace the value of a named query parameter in the URL with <key>.
    private static string MaskQueryParamValue(string url, string paramName)
    {
        // Try both the raw name and its percent-encoded form (deduped via HashSet).
        foreach (var name in new HashSet<string> { paramName, Uri.EscapeDataString(paramName) })
        {
            url = Regex.Replace(
                url,
                $@"([?&]){Regex.Escape(name)}=[^&]*",
                $"$1{name}=<key>");
        }
        return url;
    }

    private static string EscapeUrl(string url) =>
        url.Replace("\"", "%22");

    private static string EscapeHeaderValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string QuoteBody(string body)
    {
        if (!body.Contains('\''))
            return $"'{body}'";

        return $"\"{body.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
