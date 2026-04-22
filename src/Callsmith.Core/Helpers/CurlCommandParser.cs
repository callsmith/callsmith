using System.Text;
using Callsmith.Core.Models;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Parses basic cURL command syntax into a request shape suitable for the request editor.
/// </summary>
public static class CurlCommandParser
{
    // Value-consuming flags recognised:
    //   Method:  -X/--request, -I/--head (no-value), -G/--get (no-value)
    //   Headers: -H/--header, -A/--user-agent, -e/--referer, -b/--cookie
    //   Body:    -d/--data*, --json, -F/--form, --form-string
    //   Auth:    -u/--user, --oauth2-bearer
    //   URL:     --url, --url-query
    // Unknown flags are skipped along with their values (unless the value looks like a URL).

    /// <summary>
    /// Parses a cURL command. Returns <see langword="false"/> for non-cURL text or invalid input.
    /// </summary>
    public static bool TryParse(string? text, out ParsedCurlRequest? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Fast early-exit before any expensive normalization/tokenization work.
        // Must start with "curl" (case-insensitive) followed by whitespace or end-of-string.
        var trimmedSpan = text.AsSpan().TrimStart();
        if (!trimmedSpan.StartsWith("curl", StringComparison.OrdinalIgnoreCase) ||
            (trimmedSpan.Length > 4 && !char.IsWhiteSpace(trimmedSpan[4])))
            return false;

        var normalized = NormalizeLineContinuations(text);
        if (!TryTokenize(normalized, out var tokens))
            return false;

        if (tokens.Count == 0)
            return false;

        var method = "GET";
        var explicitMethod = false;
        var url = string.Empty;
        var dataAsQuery = false;
        var headers = new List<RequestKv>();
        var formDataParts = new List<string>();
        var multipartParts = new List<KeyValuePair<string, string>>();
        var multipartFileParts = new List<MultipartFilePart>();
        var urlQueryParts = new List<string>();
        var hasJsonFlag = false;
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

            if (string.Equals(token, "-G", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--get", StringComparison.OrdinalIgnoreCase))
            {
                dataAsQuery = true;
                method = "GET";
                explicitMethod = true;
                continue;
            }

            if (token.Equals("-d", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("--data", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                formDataParts.Add(value);
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

            if (string.Equals(token, "--url-query", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                urlQueryParts.Add(value);
                continue;
            }

            if (string.Equals(token, "--json", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                formDataParts.Add(value);
                hasJsonFlag = true;
                continue;
            }

            if (string.Equals(token, "--oauth2-bearer", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                auth = new AuthConfig
                {
                    AuthType = AuthConfig.AuthTypes.Bearer,
                    Token = value,
                };
                continue;
            }

            if (string.Equals(token, "-A", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--user-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                headers.Add(new RequestKv("User-Agent", value, IsEnabled: true));
                continue;
            }

            if (string.Equals(token, "-e", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--referer", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                headers.Add(new RequestKv("Referer", value, IsEnabled: true));
                continue;
            }

            if (string.Equals(token, "-b", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--cookie", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                headers.Add(new RequestKv("Cookie", value, IsEnabled: true));
                continue;
            }

            if (string.Equals(token, "-F", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--form", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "--form-string", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryTakeValue(tokens, ref i, out var value))
                    return false;
                var eq = value.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    var name = value[..eq];
                    var content = value[(eq + 1)..];
                    var isFormString = string.Equals(token, "--form-string", StringComparison.OrdinalIgnoreCase);
                    if (isFormString || !content.StartsWith("@", StringComparison.Ordinal))
                    {
                        multipartParts.Add(new KeyValuePair<string, string>(name, content));
                    }
                    else
                    {
                        // --form file reference syntax: key=@/path/to/file[;type=...]
                        var semicolon = content.IndexOf(';');
                        var filePath = semicolon >= 0 ? content[1..semicolon] : content[1..];
                        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                        {
                            var bytes = File.ReadAllBytes(filePath);
                            multipartFileParts.Add(new MultipartFilePart
                            {
                                Key = name,
                                FileBytes = bytes,
                                FileName = Path.GetFileName(filePath),
                                FilePath = filePath,
                            });
                        }
                    }
                }
                continue;
            }

            // Unknown flag: skip it. If the next token does not start with '-' and does not
            // look like a URL (no '://'), treat it as the flag's value and skip it too.
            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                if (i + 1 < tokens.Count)
                {
                    var next = tokens[i + 1];
                    if (!next.StartsWith("-", StringComparison.Ordinal) &&
                        !next.Contains("://", StringComparison.Ordinal))
                    {
                        i++; // consume the unknown flag's value
                    }
                }
                continue;
            }

            // Non-flag token: only accept as the URL when it looks like a URL.
            if (token.Contains("://", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(url))
            {
                url = token;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
            return false;

        var baseUrl = QueryStringHelper.GetBaseUrl(url);
        var queryParams = new List<RequestKv>(
            QueryStringHelper
                .ParseQueryParams(url)
                .Select(p => new RequestKv(p.Key, p.Value, IsEnabled: true)));

        // --url-query appends extra parameters to the query string.
        foreach (var part in urlQueryParts)
            queryParams.AddRange(ParseFormLikePairs(part).Select(p => new RequestKv(p.Key, p.Value, IsEnabled: true)));

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

                // --json implies JSON body type when no explicit Content-Type header overrides it.
                if (hasJsonFlag && bodyType == CollectionRequest.BodyTypes.Text)
                    bodyType = CollectionRequest.BodyTypes.Json;

                if (bodyType == CollectionRequest.BodyTypes.Form)
                {
                    formParams = ParseFormLikePairs(body);
                    body = null;
                }
            }
        }
        else if (multipartParts.Count > 0)
        {
            // -F / --form / --form-string without any -d body → multipart body.
            bodyType = CollectionRequest.BodyTypes.Multipart;
            formParams = multipartParts;
        }
        else if (multipartFileParts.Count > 0)
        {
            bodyType = CollectionRequest.BodyTypes.Multipart;
        }

        if (!explicitMethod && (formDataParts.Count > 0 || multipartParts.Count > 0 || multipartFileParts.Count > 0) && !dataAsQuery)
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
            MultipartFormFiles = multipartFileParts,
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

        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
            contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            return CollectionRequest.BodyTypes.Json;
        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            return CollectionRequest.BodyTypes.Xml;
        if (contentType.Contains("yaml", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("yml", StringComparison.OrdinalIgnoreCase))
            return CollectionRequest.BodyTypes.Yaml;
        if (contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return CollectionRequest.BodyTypes.Form;

        return CollectionRequest.BodyTypes.Text;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseFormLikePairs(string raw)
    {
        var parsed = QueryStringHelper.ParseQueryParams($"https://placeholder.local/?{raw}");
        return parsed.ToList();
    }
}
