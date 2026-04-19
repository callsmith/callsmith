using System.Globalization;
using System.Text;
using Callsmith.Core.Models;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Parses basic cURL command syntax into a request shape suitable for the request editor.
/// </summary>
public static class CurlCommandParser
{
    private static readonly HashSet<string> DataFlags =
    [
        "-d",
        "--data",
        "--data-raw",
        "--data-binary",
        "--data-ascii",
        "--data-urlencode",
    ];

    /// <summary>
    /// Parses a cURL command. Returns <see langword="false"/> for non-cURL text or invalid input.
    /// </summary>
    public static bool TryParse(string? text, out ParsedCurlRequest? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeLineContinuations(text);
        if (!TryTokenize(normalized, out var tokens))
            return false;

        if (tokens.Count == 0 || !string.Equals(tokens[0], "curl", StringComparison.OrdinalIgnoreCase))
            return false;

        var method = "GET";
        var explicitMethod = false;
        var url = string.Empty;
        var dataAsQuery = false;
        var headers = new List<RequestKv>();
        var formDataParts = new List<string>();
        AuthConfig? auth = null;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (string.Equals(token, "-X", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--request", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                method = value.ToUpperInvariant();
                explicitMethod = true;
                continue;
            }

            if (string.Equals(token, "-I", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--head", StringComparison.OrdinalIgnoreCase))
            {
                method = "HEAD";
                explicitMethod = true;
                continue;
            }

            if (DataFlags.Contains(token))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                formDataParts.Add(value);
                continue;
            }

            if (string.Equals(token, "-G", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--get", StringComparison.OrdinalIgnoreCase))
            {
                dataAsQuery = true;
                method = "GET";
                explicitMethod = true;
                continue;
            }

            if (string.Equals(token, "-H", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--header", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var headerRaw))
                    return false;

                var colon = headerRaw.IndexOf(':');
                if (colon <= 0)
                    continue;

                var key = headerRaw[..colon].Trim();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var value = headerRaw[(colon + 1)..].TrimStart();
                headers.Add(new RequestKv(key, value, IsEnabled: true));
                continue;
            }

            if (string.Equals(token, "-u", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--user", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var userRaw))
                    return false;

                var split = userRaw.IndexOf(':');
                if (split < 0)
                {
                    auth = new AuthConfig
                    {
                        AuthType = AuthConfig.AuthTypes.Basic,
                        Username = userRaw,
                        Password = string.Empty,
                    };
                }
                else
                {
                    auth = new AuthConfig
                    {
                        AuthType = AuthConfig.AuthTypes.Basic,
                        Username = userRaw[..split],
                        Password = userRaw[(split + 1)..],
                    };
                }

                continue;
            }

            if (string.Equals(token, "--url", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                url = value;
                continue;
            }

            if (!token.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(url))
            {
                url = token;
                continue;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
            return false;

        var baseUrl = QueryStringHelper.GetBaseUrl(url);
        var queryParams = new List<RequestKv>(
            QueryStringHelper
                .ParseQueryParams(url)
                .Select(p => new RequestKv(p.Key, p.Value, IsEnabled: true)));

        string bodyType = CollectionRequest.BodyTypes.None;
        string? body = null;
        IReadOnlyList<KeyValuePair<string, string>> formParams = [];

        if (formDataParts.Count > 0)
        {
            if (dataAsQuery)
            {
                foreach (var part in formDataParts)
                    queryParams.AddRange(ParseFormLikePairs(part).Select(p => new RequestKv(p.Key, p.Value, IsEnabled: true)));
            }
            else
            {
                body = string.Join("&", formDataParts);
                var contentType = headers
                    .FirstOrDefault(h => string.Equals(h.Key, WellKnownHeaders.ContentType, StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                bodyType = InferBodyType(contentType);

                if (bodyType == CollectionRequest.BodyTypes.Form)
                {
                    formParams = ParseFormLikePairs(body);
                    body = null;
                }
            }
        }

        if (!explicitMethod && formDataParts.Count > 0 && !dataAsQuery)
            method = "POST";

        parsed = new ParsedCurlRequest
        {
            Method = method,
            Url = baseUrl,
            Headers = headers,
            QueryParams = queryParams,
            BodyType = bodyType,
            Body = body,
            FormParams = formParams,
            Auth = auth ?? new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
        };

        return true;
    }

    private static string NormalizeLineContinuations(string command)
    {
        return command
            .Replace("\\\r\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal)
            .Replace("^\r\n", " ", StringComparison.Ordinal)
            .Replace("^\n", " ", StringComparison.Ordinal);
    }

    private static bool TryTokenize(string command, out List<string> tokens)
    {
        tokens = [];
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var escaping = false;

        foreach (var ch in command)
        {
            if (escaping)
            {
                current.Append(ch);
                escaping = false;
                continue;
            }

            if (inSingle)
            {
                if (ch == '\'')
                    inSingle = false;
                else
                    current.Append(ch);
                continue;
            }

            if (inDouble)
            {
                if (ch == '"')
                {
                    inDouble = false;
                }
                else if (ch == '\\')
                {
                    escaping = true;
                }
                else
                {
                    current.Append(ch);
                }
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if (ch == '\'')
            {
                inSingle = true;
                continue;
            }

            if (ch == '"')
            {
                inDouble = true;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            current.Append(ch);
        }

        if (escaping || inSingle || inDouble)
            return false;

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return true;
    }

    private static bool TryTakeValue(IReadOnlyList<string> tokens, ref int index, out string value)
    {
        if (index + 1 >= tokens.Count)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = tokens[index];
        return true;
    }

    private static string InferBodyType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return CollectionRequest.BodyTypes.Text;

        var lowered = contentType.ToLower(CultureInfo.InvariantCulture);

        if (lowered.Contains("application/json", StringComparison.Ordinal) || lowered.EndsWith("+json", StringComparison.Ordinal))
            return CollectionRequest.BodyTypes.Json;
        if (lowered.Contains("xml", StringComparison.Ordinal))
            return CollectionRequest.BodyTypes.Xml;
        if (lowered.Contains("yaml", StringComparison.Ordinal) || lowered.Contains("yml", StringComparison.Ordinal))
            return CollectionRequest.BodyTypes.Yaml;
        if (lowered.Contains("application/x-www-form-urlencoded", StringComparison.Ordinal))
            return CollectionRequest.BodyTypes.Form;

        return CollectionRequest.BodyTypes.Text;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseFormLikePairs(string raw)
    {
        var parsed = QueryStringHelper.ParseQueryParams($"https://placeholder.local/?{raw}");
        return parsed.ToList();
    }
}
