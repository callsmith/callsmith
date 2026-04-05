using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Import;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;
using ApiKeyLocations = Callsmith.Core.Models.AuthConfig.ApiKeyLocations;
using AuthTypes = Callsmith.Core.Models.AuthConfig.AuthTypes;
using BodyTypes = Callsmith.Core.Models.CollectionRequest.BodyTypes;

namespace Callsmith.Core.Postman;

/// <summary>
/// Imports Postman Collection Format v2.0 / v2.1 JSON files into the
/// Callsmith <see cref="ImportedCollection"/> domain model.
/// </summary>
/// <remarks>
/// Postman dynamic variables (<c>{{$guid}}</c>, <c>{{$randomEmail}}</c>, etc.)
/// are extracted from request fields and stored as <see cref="ImportedDynamicVariable"/>
/// entries in the global environment, mirroring how Insomnia faker tags are handled.
/// Postman pre-request and test scripts are silently ignored.
/// </remarks>
public sealed class PostmanCollectionImporter : ICollectionImporter
{
    private const string PostmanSchemaMarker = "schema.getpostman.com/json/collection/v2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ILogger<PostmanCollectionImporter> _logger;

    public PostmanCollectionImporter(ILogger<PostmanCollectionImporter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string FormatName => "Postman";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedFileExtensions { get; } = [".json"];

    /// <inheritdoc/>
    public async Task<bool> CanImportAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            // Read enough lines to find the schema URL in the "info" block.
            for (var i = 0; i < 20; i++)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Contains(PostmanSchemaMarker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CanImportAsync: could not read '{FilePath}'", filePath);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<ImportedCollection> ImportAsync(string filePath, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        PostmanDocument doc;
        try
        {
            doc = JsonSerializer.Deserialize<PostmanDocument>(json, JsonOptions)
                  ?? throw new InvalidOperationException("Deserialized result was null.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize Postman collection from '{filePath}'.", ex);
        }

        // Accumulates global dynamic vars extracted from inline {{$name}} tokens.
        var globalVars = new Dictionary<string, ImportedDynamicVariable>(StringComparer.Ordinal);

        var rootRequests = new List<ImportedRequest>();
        var rootFolders = new List<ImportedFolder>();
        var rootOrder = new List<string>();

        foreach (var item in doc.Item)
        {
            if (item.IsRequest)
            {
                var req = MapRequest(item, doc.Auth, globalVars);
                rootRequests.Add(req);
                rootOrder.Add(req.Name);
            }
            else
            {
                var folder = MapFolder(item, doc.Auth, globalVars);
                rootFolders.Add(folder);
                rootOrder.Add(folder.Name);
            }
        }

        // Collection-level variables become a dedicated environment.
        var environments = MapCollectionVariables(doc.Variable);

        return new ImportedCollection
        {
            Name = string.IsNullOrWhiteSpace(doc.Info.Name) ? "Imported Collection" : doc.Info.Name,
            RootRequests = rootRequests,
            RootFolders = rootFolders,
            ItemOrder = rootOrder,
            Environments = environments,
            GlobalDynamicVars = [.. globalVars.Values],
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mapping helpers
    // ─────────────────────────────────────────────────────────────────────────

    private ImportedRequest MapRequest(
        PostmanItem item,
        PostmanAuth? collectionAuth,
        Dictionary<string, ImportedDynamicVariable> globalVars)
    {
        var req = item.Request!;
        var method = ParseMethod(req.Method);
        var (url, queryParams, pathParams) = ParseUrl(req.Url, globalVars);
        var headers = MapHeaders(req.Header, globalVars);
        var (bodyType, bodyContent, formParams) = MapBody(req.Body, globalVars);
        var auth = MapAuth(req.Auth, collectionAuth);
        var description = ExtractDescription(req.Description);

        return new ImportedRequest
        {
            Name = string.IsNullOrWhiteSpace(item.Name) ? ImporterConstants.UnnamedRequest : item.Name,
            Method = method,
            Url = url,
            Description = description,
            Headers = headers,
            Auth = auth,
            BodyType = bodyType,
            Body = bodyContent,
            FormParams = formParams,
            PathParams = pathParams,
            QueryParams = queryParams,
        };
    }

    private ImportedFolder MapFolder(
        PostmanItem item,
        PostmanAuth? collectionAuth,
        Dictionary<string, ImportedDynamicVariable> globalVars)
    {
        var requests = new List<ImportedRequest>();
        var subFolders = new List<ImportedFolder>();
        var order = new List<string>();

        // Determine effective auth: folder items can have their own auth.
        // Postman folders don't carry an auth block directly, so collection-level
        // auth flows through unchanged.
        foreach (var child in item.Item ?? [])
        {
            if (child.IsRequest)
            {
                var req = MapRequest(child, collectionAuth, globalVars);
                requests.Add(req);
                order.Add(req.Name);
            }
            else
            {
                var folder = MapFolder(child, collectionAuth, globalVars);
                subFolders.Add(folder);
                order.Add(folder.Name);
            }
        }

        return new ImportedFolder
        {
            Name = string.IsNullOrWhiteSpace(item.Name) ? ImporterConstants.UnnamedFolder : item.Name,
            Requests = requests,
            SubFolders = subFolders,
            ItemOrder = order,
        };
    }

    private static IReadOnlyList<ImportedEnvironment> MapCollectionVariables(
        List<PostmanVariable>? variables)
    {
        if (variables is null || variables.Count == 0) return [];

        var staticVars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in variables)
        {
            if (string.IsNullOrEmpty(v.Key)) continue;
            staticVars[v.Key] = v.Value ?? string.Empty;
        }

        if (staticVars.Count == 0) return [];

        return
        [
            new ImportedEnvironment
            {
                Name = "Postman Variables",
                Variables = staticVars,
            }
        ];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // URL parsing
    // ─────────────────────────────────────────────────────────────────────────

    private (string url, IReadOnlyList<RequestKv> queryParams, IReadOnlyDictionary<string, string> pathParams)
        ParseUrl(JsonElement urlElement, Dictionary<string, ImportedDynamicVariable> globalVars)
    {
        if (urlElement.ValueKind == JsonValueKind.String)
        {
            return ParseUrlFromString(urlElement.GetString() ?? string.Empty, globalVars);
        }

        if (urlElement.ValueKind == JsonValueKind.Object)
        {
            var urlObj = urlElement.Deserialize<PostmanUrl>(JsonOptions);
            if (urlObj is null)
                return (string.Empty, [], new Dictionary<string, string>());

            // Use the raw string as the canonical URL (already fully formed).
            var rawUrl = ExtractDynamicVars(urlObj.Raw ?? string.Empty, globalVars);

            // Parse query params from the structured "query" array.
            var queryParams = new List<RequestKv>();
            foreach (var q in urlObj.Query ?? [])
            {
                if (string.IsNullOrEmpty(q.Key)) continue;
                var value = ExtractDynamicVars(q.Value ?? string.Empty, globalVars);
                queryParams.Add(new RequestKv(q.Key, value, q.Disabled != true));
            }

            // Path params from the "variable" array.
            var pathParams = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in urlObj.Variable ?? [])
            {
                if (string.IsNullOrEmpty(v.Key)) continue;
                pathParams[v.Key] = ExtractDynamicVars(v.Value ?? string.Empty, globalVars);
            }

            return (rawUrl, queryParams, pathParams);
        }

        return (string.Empty, [], new Dictionary<string, string>());
    }

    /// <summary>
    /// Parses a URL plain string into its URL, query params, and infers path params
    /// from <c>{{varName}}</c> segments in the path portion.
    /// </summary>
    private (string url, IReadOnlyList<RequestKv> queryParams, IReadOnlyDictionary<string, string> pathParams)
        ParseUrlFromString(string raw, Dictionary<string, ImportedDynamicVariable> globalVars)
    {
        var url = ExtractDynamicVars(raw, globalVars);

        // Split off query string to extract query params separately.
        var queryIndex = url.IndexOf('?');
        var queryParams = new List<RequestKv>();
        if (queryIndex >= 0)
        {
            var queryString = url[(queryIndex + 1)..];
            url = url[..queryIndex];
            foreach (var part in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = part.IndexOf('=');
                if (eqIdx < 0)
                {
                    queryParams.Add(new RequestKv(part, string.Empty));
                }
                else
                {
                    queryParams.Add(new RequestKv(part[..eqIdx], part[(eqIdx + 1)..]));
                }
            }
        }

        // Path params: any {{varName}} segment that looks like a placeholder in the path.
        // These are already represented as {{varName}} in the URL string itself; we collect
        // their names from path segments that are *pure* {{var}} references.
        var pathParams = new Dictionary<string, string>(StringComparer.Ordinal);
        var uriPath = url;
        // Strip protocol+host: look for first '/' after "://" or from start.
        var schemeEnd = uriPath.IndexOf("://", StringComparison.Ordinal);
        var pathStart = schemeEnd >= 0
            ? uriPath.IndexOf('/', schemeEnd + 3)
            : uriPath.IndexOf('/');

        if (pathStart >= 0)
        {
            var pathPart = uriPath[(pathStart + 1)..];
            foreach (var segment in pathPart.Split('/'))
            {
                var m = PureTemplateVar.Match(segment);
                if (m.Success)
                    pathParams[m.Groups[1].Value] = string.Empty;
            }
        }

        return (url, queryParams, pathParams);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Headers
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<RequestKv> MapHeaders(
        List<PostmanHeader>? headers,
        Dictionary<string, ImportedDynamicVariable> globalVars)
    {
        var result = new List<RequestKv>();
        foreach (var h in headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(h.Key)) continue;
            var value = ExtractDynamicVars(h.Value ?? string.Empty, globalVars);
            result.Add(new RequestKv(h.Key, value, h.Disabled != true));
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body
    // ─────────────────────────────────────────────────────────────────────────

    private static (string bodyType, string? bodyContent, IReadOnlyList<KeyValuePair<string, string>> formParams)
        MapBody(PostmanBody? body, Dictionary<string, ImportedDynamicVariable> globalVars)
    {
        if (body is null) return (BodyTypes.None, null, []);

        switch (body.Mode?.ToLowerInvariant())
        {
            case "raw":
            {
                var raw = body.Raw ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw)) return (BodyTypes.None, null, []);

                var language = body.Options?.Raw?.Language?.ToLowerInvariant();
                var bodyType = language switch
                {
                    "json"       => BodyTypes.Json,
                    "xml"        => BodyTypes.Xml,
                    "html"       => BodyTypes.Text,
                    "javascript" => BodyTypes.Text,
                    _            => LooksLikeJson(raw) ? BodyTypes.Json : BodyTypes.Text,
                };

                var content = ExtractDynamicVars(raw, globalVars);
                return (bodyType, content, []);
            }

            case "formdata":
            {
                var formParams = MapFormParams(body.Formdata, globalVars);
                return (BodyTypes.Multipart, null, formParams);
            }

            case "urlencoded":
            {
                var formParams = MapFormParams(body.Urlencoded, globalVars);
                return (BodyTypes.Form, null, formParams);
            }

            case "graphql":
            {
                if (body.GraphQL is null) return (BodyTypes.Text, null, []);
                var sb = new StringBuilder();
                sb.AppendLine(body.GraphQL.Query ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(body.GraphQL.Variables))
                    sb.AppendLine(body.GraphQL.Variables);
                var content = ExtractDynamicVars(sb.ToString().TrimEnd(), globalVars);
                return (BodyTypes.Text, content, []);
            }

            default:
                return (BodyTypes.None, null, []);
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> MapFormParams(
        List<PostmanFormParam>? items,
        Dictionary<string, ImportedDynamicVariable> globalVars)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var p in items ?? [])
        {
            if (string.IsNullOrEmpty(p.Key) || p.Disabled == true) continue;
            var value = ExtractDynamicVars(p.Value ?? string.Empty, globalVars);
            result.Add(new KeyValuePair<string, string>(p.Key, value));
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auth
    // ─────────────────────────────────────────────────────────────────────────

    private static AuthConfig MapAuth(PostmanAuth? requestAuth, PostmanAuth? collectionAuth)
    {
        // "noauth" at request level explicitly disables auth for that request.
        if (string.Equals(requestAuth?.Type, "noauth", StringComparison.OrdinalIgnoreCase))
            return new AuthConfig { AuthType = AuthTypes.None };

        // Use request-level auth if defined, otherwise fall back to collection-level.
        var auth = requestAuth ?? collectionAuth;
        return BuildAuthConfig(auth);
    }

    private static AuthConfig BuildAuthConfig(PostmanAuth? auth)
    {
        switch (auth?.Type?.ToLowerInvariant())
        {
            case "bearer":
            {
                var token = FindAuthKvValue(auth.Bearer, "token");
                return new AuthConfig { AuthType = AuthTypes.Bearer, Token = token };
            }

            case "basic":
            {
                var username = FindAuthKvValue(auth.Basic, "username");
                var password = FindAuthKvValue(auth.Basic, "password");
                return new AuthConfig
                {
                    AuthType = AuthTypes.Basic,
                    Username = username,
                    Password = password,
                };
            }

            case "apikey":
            {
                var name  = FindAuthKvValue(auth.Apikey, "key");
                var value = FindAuthKvValue(auth.Apikey, "value");
                var inStr = FindAuthKvValue(auth.Apikey, "in");
                var apiKeyIn = string.Equals(inStr, "query", StringComparison.OrdinalIgnoreCase)
                    ? ApiKeyLocations.Query
                    : ApiKeyLocations.Header;
                return new AuthConfig
                {
                    AuthType = AuthTypes.ApiKey,
                    ApiKeyName = name,
                    ApiKeyValue = value,
                    ApiKeyIn = apiKeyIn,
                };
            }

            default:
                return new AuthConfig { AuthType = AuthTypes.None };
        }
    }

    /// <summary>Extracts a string value from a Postman auth key-value list by key name.</summary>
    private static string? FindAuthKvValue(List<PostmanAuthKv>? kvList, string key)
    {
        if (kvList is null) return null;
        var match = kvList.FirstOrDefault(kv =>
            string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        return match.Value.ValueKind switch
        {
            JsonValueKind.String => match.Value.GetString(),
            JsonValueKind.Number => match.Value.GetRawText(),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            _                    => null,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dynamic variable extraction  ({{$name}} → MockData global var)
    // ─────────────────────────────────────────────────────────────────────────

    // Matches {{$anyName}} — Postman built-in dynamic variable syntax.
    private static readonly Regex PostmanDynamicVar =
        new(@"\{\{\$([A-Za-z][A-Za-z0-9_]*)\}\}", RegexOptions.Compiled);

    // Matches a path segment that is *entirely* a {{varName}} reference.
    private static readonly Regex PureTemplateVar =
        new(@"^\{\{([^}]+)\}\}$", RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="value"/> for <c>{{$name}}</c> Postman dynamic variable tokens.
    /// Each recognised token is:
    /// <list type="bullet">
    ///   <item>Resolved to a <c>(MockDataCategory, MockDataField)</c> pair via <see cref="PostmanDynamicVarMap"/>.</item>
    ///   <item>Added to <paramref name="globalVars"/> (deduplicated by var name).</item>
    ///   <item>Replaced in the output string with a plain <c>{{var-name}}</c> reference.</item>
    /// </list>
    /// Unrecognised tokens (e.g. <c>{{$timestamp}}</c>) are left as-is.
    /// </summary>
    internal static string ExtractDynamicVars(
        string value,
        Dictionary<string, ImportedDynamicVariable> globalVars)
    {
        if (!value.Contains("{{$", StringComparison.Ordinal)) return value;

        return PostmanDynamicVar.Replace(value, match =>
        {
            var tokenName = match.Groups[1].Value;

            if (!PostmanDynamicVarMap.TryGetValue(tokenName, out var mapping))
            {
                // Not a known dynamic var — leave unchanged.
                return match.Value;
            }

            var (category, field) = mapping;
            var varName = $"{category.ToLowerInvariant().Replace(' ', '-')}-{field.ToLowerInvariant().Replace(' ', '-')}";

            if (!globalVars.ContainsKey(varName))
            {
                globalVars[varName] = new ImportedDynamicVariable
                {
                    Name = varName,
                    MockDataCategory = category,
                    MockDataField = field,
                };
            }

            return $"{{{{{varName}}}}}";
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Postman dynamic variable → MockData mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps Postman's built-in dynamic variable names (without the leading $) to
    /// Callsmith <see cref="MockDataCatalog"/> (category, field) pairs.
    /// Tokens not present in this map are left as literal text during import.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Category, string Field)> PostmanDynamicVarMap =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            // ── Random ──────────────────────────────────────────────────────
            ["guid"]               = ("Random", "UUID"),
            ["randomUUID"]         = ("Random", "UUID"),
            ["randomInt"]          = ("Random", "Number"),
            ["randomNumber"]       = ("Random", "Number"),
            ["randomAlphaNumeric"] = ("Random", "Alpha-Numeric"),
            ["randomBoolean"]      = ("Random", "Boolean"),
            ["randomHexadecimal"]  = ("Random", "Hash (MD5)"),

            // ── Name ────────────────────────────────────────────────────────
            ["randomFirstName"]    = ("Name", "First Name"),
            ["randomLastName"]     = ("Name", "Last Name"),
            ["randomFullName"]     = ("Name", "Full Name"),
            ["randomNamePrefix"]   = ("Name", "Prefix"),
            ["randomNameSuffix"]   = ("Name", "Suffix"),
            ["randomJobTitle"]     = ("Name", "Job Title"),
            ["randomJobArea"]      = ("Name", "Job Title"),
            ["randomJobType"]      = ("Name", "Job Title"),
            ["randomJobDescriptor"]= ("Name", "Job Title"),

            // ── Internet ────────────────────────────────────────────────────
            ["randomEmail"]        = ("Internet", "Email"),
            ["randomExampleEmail"] = ("Internet", "Example Email"),
            ["randomUserName"]     = ("Internet", "Username"),
            ["randomUrl"]          = ("Internet", "URL"),
            ["randomPassword"]     = ("Internet", "Password"),
            ["randomIP"]           = ("Internet", "IP Address"),
            ["randomIPV6"]         = ("Internet", "IPv6 Address"),
            ["randomMACAddress"]   = ("Internet", "MAC Address"),
            ["randomDomainName"]   = ("Internet", "Domain Name"),
            ["randomDomainSuffix"] = ("Internet", "Domain Name"),
            ["randomDomainWord"]   = ("Internet", "Domain Name"),
            ["randomProtocol"]     = ("Internet", "URL"),

            // ── Phone ───────────────────────────────────────────────────────
            ["randomPhoneNumber"]           = ("Phone", "Phone Number"),
            ["randomPhoneNumberExt"]        = ("Phone", "Phone Number"),

            // ── Address ─────────────────────────────────────────────────────
            ["randomStreetAddress"] = ("Address", "Full Address"),
            ["randomStreetName"]    = ("Address", "Full Address"),
            ["randomCity"]          = ("Address", "City"),
            ["randomCountry"]       = ("Address", "Country"),
            ["randomCountryCode"]   = ("Address", "Country Code"),
            ["randomState"]         = ("Address", "State"),
            ["randomStateAbbr"]     = ("Address", "State Abbreviation"),
            ["randomZipCode"]       = ("Address", "Zip Code"),
            ["randomLatitude"]      = ("Address", "Latitude"),
            ["randomLongitude"]     = ("Address", "Longitude"),

            // ── Lorem ───────────────────────────────────────────────────────
            ["randomLoremWord"]      = ("Lorem", "Word"),
            ["randomLoremWords"]     = ("Lorem", "Word"),
            ["randomWord"]           = ("Lorem", "Word"),
            ["randomWords"]          = ("Lorem", "Word"),
            ["randomNoun"]           = ("Lorem", "Word"),
            ["randomAdjective"]      = ("Lorem", "Word"),
            ["randomVerb"]           = ("Lorem", "Word"),
            ["randomIngverb"]        = ("Lorem", "Word"),
            ["randomLoremSentence"]  = ("Lorem", "Sentence"),
            ["randomLoremSentences"] = ("Lorem", "Sentence"),
            ["randomSentence"]       = ("Lorem", "Sentence"),
            ["randomLoremParagraph"] = ("Lorem", "Paragraph"),
            ["randomLoremParagraphs"]= ("Lorem", "Paragraph"),
            ["randomLoremText"]      = ("Lorem", "Paragraph"),
            ["randomLoremSlug"]      = ("Lorem", "Slug"),

            // ── Finance ─────────────────────────────────────────────────────
            ["randomPrice"]          = ("Finance", "Amount"),
            ["randomAmount"]         = ("Finance", "Amount"),
            ["randomCurrencyName"]   = ("Finance", "Currency Name"),
            ["randomCurrencyCode"]   = ("Finance", "Currency Code"),
            ["randomCurrencySymbol"] = ("Finance", "Currency Code"),
            ["randomBitcoin"]        = ("Finance", "Bitcoin"),
            ["randomBitcoinAddress"] = ("Finance", "Bitcoin"),
            ["randomCreditCardMask"] = ("Finance", "Credit Card"),
            ["randomBankAccount"]    = ("Finance", "IBAN"),
            ["randomBankAccountName"]= ("Finance", "IBAN"),
            ["randomBankAccountBic"] = ("Finance", "IBAN"),
            ["randomBankAccountIban"]= ("Finance", "IBAN"),

            // ── Finance (additions) ──────────────────────────────────────
            ["randomTransactionType"]     = ("Finance", "Transaction Type"),

            // ── Company ─────────────────────────────────────────────────────
            ["randomCompanyName"]  = ("Company", "Company Name"),
            ["randomCompanySuffix"]= ("Company", "Company Name"),
            ["randomCatchPhrase"]  = ("Company", "Catch Phrase"),
            ["randomBs"]           = ("Company", "Buzzwords"),
            ["randomBsAdjective"]  = ("Company", "Buzzwords"),
            ["randomBsNoun"]       = ("Company", "Buzzwords"),
            ["randomBsVerb"]       = ("Company", "Buzzwords"),

            // ── Company (additions) ──────────────────────────────────────
            ["randomCatchPhraseAdjective"]  = ("Company", "Catch Phrase Adjective"),
            ["randomCatchPhraseDescriptor"] = ("Company", "Catch Phrase Descriptor"),
            ["randomCatchPhraseNoun"]       = ("Company", "Catch Phrase Noun"),

            // ── Date ────────────────────────────────────────────────────────
            ["randomDatePast"]    = ("Date", "Past Date"),
            ["randomDateFuture"]  = ("Date", "Future Date"),
            ["randomDateRecent"]  = ("Date", "Recent Date"),
            ["randomDateMonth"]   = ("Date", "Recent Date"),
            ["randomDateWeekday"] = ("Date", "Recent Date"),
            ["randomDateBetween"] = ("Date", "Past Date"),

            // ── Date (timestamps — native .NET) ─────────────────────────────
            ["timestamp"]    = ("Date", "Timestamp"),
            ["isoTimestamp"] = ("Date", "ISO Timestamp"),

            // ── Random (additions) ───────────────────────────────────────────
            ["randomExponent"]  = ("Random", "Number"),
            ["randomObjectId"]  = ("Random", "Object ID"),
            ["randomLocale"]    = ("Random", "Locale"),

            // ── Phone (additions) ────────────────────────────────────────────
            ["randomPhone"]  = ("Phone", "Phone Number"),

            // ── Internet (additions) ─────────────────────────────────────────
            ["randomColor"]        = ("Internet", "Color"),
            ["randomUserAgent"]    = ("Internet", "User Agent"),
            ["randomAbbreviation"] = ("Internet", "Abbreviation"),
            ["randomAvatarImage"]  = ("Internet", "Avatar URL"),
            ["randomImageUrl"]     = ("Internet", "Image URL"),
            ["randomImageDataUri"] = ("Internet", "Image URL"),

            // ── System ───────────────────────────────────────────────────────
            ["randomMimeType"]      = ("System", "MIME Type"),
            ["randomFileName"]      = ("System", "File Name"),
            ["randomFileType"]      = ("System", "File Type"),
            ["randomFileExt"]       = ("System", "File Extension"),
            ["randomFilePath"]      = ("System", "File Path"),
            ["randomDirectoryPath"] = ("System", "Directory Path"),
            ["randomSemver"]        = ("System", "Semver"),
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static HttpMethod ParseMethod(string? method) =>
        method?.ToUpperInvariant() switch
        {
            "GET"     => HttpMethod.Get,
            "POST"    => HttpMethod.Post,
            "PUT"     => HttpMethod.Put,
            "PATCH"   => HttpMethod.Patch,
            "DELETE"  => HttpMethod.Delete,
            "HEAD"    => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            _         => HttpMethod.Get,
        };

    /// <summary>
    /// Extracts a plain-text description from a Postman description field,
    /// which can be either a plain string or an object with a "content" property.
    /// </summary>
    private static string? ExtractDescription(JsonElement? descElement)
    {
        if (descElement is null) return null;
        return descElement.Value.ValueKind switch
        {
            JsonValueKind.String => descElement.Value.GetString(),
            JsonValueKind.Object when descElement.Value.TryGetProperty("content", out var content)
                => content.GetString(),
            _ => null,
        };
    }

    /// <summary>
    /// Quick heuristic: does the trimmed string start with '{' or '['?
    /// Used when Postman does not specify a language for a raw body.
    /// </summary>
    private static bool LooksLikeJson(string raw)
    {
        var trimmed = raw.AsSpan().TrimStart();
        return trimmed is ['{' or '[', ..];
    }
}
