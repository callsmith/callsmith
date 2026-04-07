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
    /// (<c>&lt;token&gt;</c> for Authorization headers, <c>&lt;key&gt;</c> for API keys),
    /// and any secret environment variable values are replaced with <c>&lt;secret&gt;</c>.
    /// </param>
    /// <param name="authMaskInfo">
    /// Additional hints for masking API key auth. Only needed when
    /// <paramref name="maskAuthentication"/> is true and the request uses API key auth.
    /// </param>
    /// <param name="secretValues">
    /// The resolved values of all secret environment variables. When
    /// <paramref name="maskAuthentication"/> is true, any occurrence of these values in the
    /// URL, headers, and body is replaced with <c>&lt;secret&gt;</c>.
    /// </param>
    public static string Build(
        RequestModel request,
        bool maskAuthentication = false,
        CurlAuthMaskInfo? authMaskInfo = null,
        IReadOnlySet<string>? secretValues = null)
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

        // URL — mask API-key query param value if needed, then mask secret values
        var url = maskAuthentication && authMaskInfo?.ApiKeyQueryParamName is { } qpName
            ? MaskQueryParamValue(request.Url, qpName)
            : request.Url;

        if (maskAuthentication && secretValues is { Count: > 0 })
            url = MaskSecretValues(url, secretValues);

        sb.Append($" \\\n  \"{EscapeUrl(url)}\"");

        // Request headers
        foreach (var (key, value) in request.Headers)
        {
            var displayValue = maskAuthentication ? MaskHeaderValue(key, value, authMaskInfo) : value;
            if (maskAuthentication && secretValues is { Count: > 0 })
                displayValue = MaskSecretValues(displayValue, secretValues);
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
        {
            var body = request.Body;
            if (maskAuthentication && secretValues is { Count: > 0 })
                body = MaskSecretValues(body, secretValues);
            sb.Append($" \\\n  --data-raw {QuoteBody(body)}");
        }

        return sb.ToString();
    }

    // Replace each known secret value with <secret>.
    // Secrets are applied longest-first to prevent a shorter secret that is a substring
    // of a longer secret from being masked first and leaving a partial longer match.
    // Empty values are skipped.
    private static string MaskSecretValues(string text, IReadOnlySet<string> secretValues)
    {
        foreach (var secret in secretValues.OrderByDescending(s => s.Length))
        {
            if (string.IsNullOrEmpty(secret)) continue;
            text = text.Replace(secret, "<secret>", StringComparison.Ordinal);
        }
        return text;
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
