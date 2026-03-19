using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Import;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using BodyTypes = Callsmith.Core.Models.CollectionRequest.BodyTypes;
using AuthTypes = Callsmith.Core.Models.AuthConfig.AuthTypes;
using ApiKeyLocations = Callsmith.Core.Models.AuthConfig.ApiKeyLocations;

namespace Callsmith.Core.Insomnia;

/// <summary>
/// Imports Insomnia collection files (REST v5, schema_version 5.x) into the
/// Callsmith <see cref="ImportedCollection"/> domain model.
/// </summary>
public sealed class InsomniaCollectionImporter : ICollectionImporter
{
    private const string InsomniaTypePrefix = "collection.insomnia.rest";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ILogger<InsomniaCollectionImporter> _logger;

    public InsomniaCollectionImporter(ILogger<InsomniaCollectionImporter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string FormatName => "Insomnia";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedFileExtensions { get; } = [".yaml", ".yml", ".json"];

    /// <inheritdoc/>
    public async Task<bool> CanImportAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            // Read just enough lines to find the "type:" header.
            for (var i = 0; i < 10; i++)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.TrimStart().StartsWith("type:", StringComparison.Ordinal)
                    && line.Contains(InsomniaTypePrefix, StringComparison.Ordinal))
                {
                    return true;
                }
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
        var yaml = await File.ReadAllTextAsync(filePath, ct);
        InsomniaDocument doc;
        try
        {
            doc = Deserializer.Deserialize<InsomniaDocument>(yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize Insomnia collection from '{filePath}'.", ex);
        }

        var rootRequests = new List<ImportedRequest>();
        var rootFolders = new List<ImportedFolder>();
        var rootOrder = new List<string>();

        // Build a map of Insomnia request IDs → display paths for dynamic variable resolution.
        var requestIdMap = BuildRequestIdMap(doc.Collection);

        // Accumulates global env vars extracted from inline request-field {% %} tags.
        // Keyed by var name so duplicates (same faker type in multiple requests) are merged.
        var globalVarsExtracted = new Dictionary<string, ImportedDynamicVariable>(StringComparer.Ordinal);

        // Insomnia sortKey values for requests/folders are large negative numbers;
        // ascending order places the most-negative (earliest created) item first,
        // which matches Insomnia's display order.
        foreach (var item in doc.Collection.OrderBy(i => i.Meta?.SortKey ?? 0))
        {
            if (item.IsRequest)
            {
                var req = MapRequest(item, requestIdMap, globalVarsExtracted);
                rootRequests.Add(req);
                rootOrder.Add(req.Name);
            }
            else
            {
                var folder = MapFolder(item, requestIdMap, globalVarsExtracted);
                rootFolders.Add(folder);
                rootOrder.Add(folder.Name);
            }
        }

        // Environments: base data + each sub-environment
        var environments = MapEnvironments(doc.Environments, requestIdMap);

        return new ImportedCollection
        {
            Name = string.IsNullOrWhiteSpace(doc.Name) ? "Imported Collection" : doc.Name,
            RootRequests = rootRequests,
            RootFolders = rootFolders,
            ItemOrder = rootOrder,
            Environments = environments,
            GlobalDynamicVars = [.. globalVarsExtracted.Values],
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mapping helpers
    // ─────────────────────────────────────────────────────────────────────────

    private ImportedRequest MapRequest(
        InsomniaCollectionItem item,
        IReadOnlyDictionary<string, string>? requestIdMap = null,
        Dictionary<string, ImportedDynamicVariable>? globalVars = null)
    {
        requestIdMap ??= new Dictionary<string, string>();
        var url = ConvertPathParamSyntax(ExtractTagsToGlobalVars(
            NormalizeVariables(item.Url ?? string.Empty), requestIdMap, globalVars));
        var method = ParseMethod(item.Method);
        var headers = MapHeaders(item.Headers, requestIdMap, globalVars);
        var auth = MapAuth(item.Authentication, requestIdMap);
        var (bodyType, bodyContent, formParams) = MapBody(item.Body, requestIdMap, globalVars);
        var pathParams = MapPathParams(item.PathParameters, requestIdMap);
        var queryParams = MapQueryParams(item.Parameters, requestIdMap, globalVars);

        return new ImportedRequest
        {
            Name = string.IsNullOrWhiteSpace(item.Name) ? "Unnamed Request" : item.Name,
            Method = method,
            Url = url,
            Description = item.Meta?.Description,
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
        InsomniaCollectionItem item,
        IReadOnlyDictionary<string, string>? requestIdMap = null,
        Dictionary<string, ImportedDynamicVariable>? globalVars = null)
    {
        requestIdMap ??= new Dictionary<string, string>();
        var requests = new List<ImportedRequest>();
        var subFolders = new List<ImportedFolder>();
        var order = new List<string>();

        foreach (var child in (item.Children ?? []).OrderBy(c => c.Meta?.SortKey ?? 0))
        {
            if (child.IsRequest)
            {
                var req = MapRequest(child, requestIdMap, globalVars);
                requests.Add(req);
                order.Add(req.Name);
            }
            else
            {
                var folder = MapFolder(child, requestIdMap, globalVars);
                subFolders.Add(folder);
                order.Add(folder.Name);
            }
        }

        return new ImportedFolder
        {
            Name = string.IsNullOrWhiteSpace(item.Name) ? "Unnamed Folder" : item.Name,
            Requests = requests,
            SubFolders = subFolders,
            ItemOrder = order,
        };
    }

    private static IReadOnlyList<ImportedEnvironment> MapEnvironments(
        InsomniaEnvironmentBlock? block,
        IReadOnlyDictionary<string, string> requestIdMap)
    {
        if (block is null) return [];

        var result = new List<ImportedEnvironment>();

        // If there are sub-environments, import each one. The base environment
        // variables are merged as defaults (sub-env values win on conflict).
        var baseData = block.Data ?? new Dictionary<string, string>();

        if (block.SubEnvironments is { Count: > 0 } subs)
        {
            foreach (var sub in subs.OrderBy(s => s.Meta?.SortKey ?? 0))
            {
                // Merge: base vars first, then sub-env overrides
                var merged = new Dictionary<string, string>(baseData);
                foreach (var kv in sub.Data ?? [])
                    merged[kv.Key] = kv.Value;

                var (staticVars, dynamicVars) = SplitVariables(merged, requestIdMap);
                result.Add(new ImportedEnvironment
                {
                    Name = string.IsNullOrWhiteSpace(sub.Name) ? "Environment" : sub.Name,
                    Variables = staticVars,
                    DynamicVariables = dynamicVars,
                    Color = sub.Color,
                });
            }
        }
        else
        {
            // Single base environment with no sub-environments
            var (staticVars, dynamicVars) = SplitVariables(baseData, requestIdMap);

            if (staticVars.Count > 0 || dynamicVars.Count > 0)
            {
                result.Add(new ImportedEnvironment
                {
                    Name = string.IsNullOrWhiteSpace(block.Name) ? "Default" : block.Name,
                    Variables = staticVars,
                    DynamicVariables = dynamicVars,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a raw Insomnia environment data dictionary into static string variables
    /// and typed dynamic variables (mock-data or response-body).
    /// </summary>
    private static (IReadOnlyDictionary<string, string> staticVars, IReadOnlyList<ImportedDynamicVariable> dynamicVars)
        SplitVariables(Dictionary<string, string> data, IReadOnlyDictionary<string, string> requestIdMap)
    {
        var staticVars = new Dictionary<string, string>();
        var dynamicVars = new List<ImportedDynamicVariable>();

        foreach (var kv in data)
        {
            if (!IsScriptValue(kv.Value))
            {
                staticVars[kv.Key] = NormalizeVariables(kv.Value);
                continue;
            }

            // Try pure faker tag
            var fakerMatch = InsomniaFakerTag.Match(kv.Value.Trim());
            if (fakerMatch.Success && fakerMatch.Value.Trim() == kv.Value.Trim())
            {
                var (cat, fld) = ResolveFakerEntry(fakerMatch.Groups[1].Value);
                dynamicVars.Add(new ImportedDynamicVariable
                {
                    Name = kv.Key,
                    MockDataCategory = cat,
                    MockDataField = fld,
                });
                continue;
            }

            // Try pure response tag
            var responseMatch = InsomniaResponseTag.Match(kv.Value.Trim());
            if (responseMatch.Success && responseMatch.Value.Trim() == kv.Value.Trim())
            {
                var requestId = responseMatch.Groups[2].Value;
                var pathRaw = responseMatch.Groups[3].Value;
                var freq = responseMatch.Groups[4].Value;
                var secs = responseMatch.Groups[5].Value;

                var requestName = requestIdMap.TryGetValue(requestId, out var n) ? n : requestId;
                var path = DecodePath(pathRaw);
                var frequency = ParseFrequency(freq);
                int? expiresAfterSeconds = int.TryParse(secs, out var s) ? s : null;

                dynamicVars.Add(new ImportedDynamicVariable
                {
                    Name = kv.Key,
                    ResponseRequestName = requestName,
                    ResponsePath = path,
                    ResponseFrequency = frequency,
                    ResponseExpiresAfterSeconds = expiresAfterSeconds,
                });
                continue;
            }

            // Composite or mixed value: static text + one or more dynamic tags.
            // Extract each tag into its own dynamic var; store the original var as a
            // static string that references the new vars with {{name}} syntax.
            var localExtracted = new Dictionary<string, ImportedDynamicVariable>();
            var transformed = ExtractTagsToGlobalVars(kv.Value, requestIdMap, localExtracted);
            if (localExtracted.Count > 0)
            {
                staticVars[kv.Key] = NormalizeVariables(transformed);
                dynamicVars.AddRange(localExtracted.Values);
            }
            else
            {
                // Couldn't extract any dynamic vars — normalize {{}} syntax but drop the var
                // silently if unresolvable {%...%} script tags remain, rather than storing
                // raw script syntax as a plain static string.
                var normalized = NormalizeVariables(kv.Value);
                if (!normalized.Contains("{%", StringComparison.Ordinal))
                    staticVars[kv.Key] = normalized;
            }
        }

        return (staticVars, dynamicVars);
    }

    private static IReadOnlyList<RequestKv> MapHeaders(
        List<InsomniaHeader>? insomniaHeaders,
        IReadOnlyDictionary<string, string> requestIdMap,
        Dictionary<string, ImportedDynamicVariable>? globalVars = null)
    {
        var headers = new List<RequestKv>();

        foreach (var h in insomniaHeaders ?? [])
        {
            if (string.IsNullOrWhiteSpace(h.Name)) continue;
            var value = ExtractTagsToGlobalVars(
                NormalizeVariables(h.Value ?? string.Empty), requestIdMap, globalVars);
            headers.Add(new RequestKv(h.Name, value, !h.Disabled));
        }

        return headers;
    }

    private static AuthConfig MapAuth(InsomniaAuthentication? auth, IReadOnlyDictionary<string, string> requestIdMap)
    {
        return (auth?.Type?.ToLowerInvariant()) switch
        {
            "bearer" => new AuthConfig
            {
                AuthType = AuthTypes.Bearer,
                Token = NormalizeAndConvertTags(auth!.Token ?? string.Empty, requestIdMap),
            },
            "basic" => new AuthConfig
            {
                AuthType = AuthTypes.Basic,
                Username = NormalizeAndConvertTags(auth!.Username ?? string.Empty, requestIdMap),
                Password = NormalizeAndConvertTags(auth!.Password ?? string.Empty, requestIdMap),
            },
            "apikey" => new AuthConfig
            {
                AuthType = AuthTypes.ApiKey,
                ApiKeyName = NormalizeAndConvertTags(auth!.Key ?? string.Empty, requestIdMap),
                ApiKeyValue = NormalizeAndConvertTags(auth!.Value ?? string.Empty, requestIdMap),
                ApiKeyIn = string.Equals(auth.AddTo, "queryParams",
                    StringComparison.OrdinalIgnoreCase)
                    ? ApiKeyLocations.Query
                    : ApiKeyLocations.Header,
            },
            _ => new AuthConfig(),
        };
    }

    private static (string bodyType, string? bodyContent, IReadOnlyList<KeyValuePair<string, string>> formParams) MapBody(
        InsomniaBody? body,
        IReadOnlyDictionary<string, string> requestIdMap,
        Dictionary<string, ImportedDynamicVariable>? globalVars = null)
    {
        if (body is null)
            return (BodyTypes.None, null, []);

        // Form-encoded bodies use a "params" KVP array, not a raw "text" field.
        if (string.Equals(body.MimeType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var formParams = (body.Params ?? [])
                .Where(p => !p.Disabled && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new KeyValuePair<string, string>(
                    p.Name,
                    ExtractTagsToGlobalVars(NormalizeVariables(p.Value ?? string.Empty), requestIdMap, globalVars)))
                .ToList();
            return (BodyTypes.Form, null, formParams);
        }

        if (string.IsNullOrEmpty(body.Text))
            return (BodyTypes.None, null, []);

        var type = body.MimeType switch
        {
            "application/json" => BodyTypes.Json,
            "text/plain" => BodyTypes.Text,
            "application/xml" or "text/xml" => BodyTypes.Xml,
            "multipart/form-data" => BodyTypes.Multipart,
            _ => string.IsNullOrWhiteSpace(body.Text) ? BodyTypes.None : BodyTypes.Text,
        };

        var bodyText = ExtractTagsToGlobalVars(
            NormalizeVariables(body.Text), requestIdMap, globalVars);
        return (type, bodyText, []);
    }

    private static IReadOnlyDictionary<string, string> MapPathParams(
        List<InsomniaPathParam>? pathParams,
        IReadOnlyDictionary<string, string> requestIdMap)
    {
        var result = new Dictionary<string, string>();

        foreach (var p in pathParams ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            result[p.Name] = NormalizeAndConvertTags(p.Value, requestIdMap);
        }

        return result;
    }

    private static IReadOnlyList<RequestKv> MapQueryParams(
        List<InsomniaQueryParam>? queryParams,
        IReadOnlyDictionary<string, string> requestIdMap,
        Dictionary<string, ImportedDynamicVariable>? globalVars = null)
    {
        var result = new List<RequestKv>();

        foreach (var p in queryParams ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            var value = ExtractTagsToGlobalVars(
                NormalizeVariables(p.Value ?? string.Empty), requestIdMap, globalVars);
            result.Add(new RequestKv(p.Name, value, !p.Disabled));
        }

        return result;
    }

    private static HttpMethod ParseMethod(string? method) =>
        (method?.ToUpperInvariant()) switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            _ => HttpMethod.Get,
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Variable normalization
    // ─────────────────────────────────────────────────────────────────────────

    // Matches Insomnia :paramName path parameter syntax in URL segments.
    // Only matches a colon followed by a word identifier at a path segment boundary.
    private static readonly Regex InsomniaPathParamSyntax =
        new(@"(?<![{\w]):([A-Za-z_][A-Za-z0-9_]*)(?=[/?#]|$)", RegexOptions.Compiled);

    // Matches Insomnia Nunjucks syntax: {{ _['var-name'] }} → {{var-name}}
    private static readonly Regex NunjucksIndexedVar =
        new(@"\{\{\s*_\['([^']+)'\]\s*\}\}", RegexOptions.Compiled);

    // Matches {{ _.varName }} → {{varName}}
    private static readonly Regex NunjucksDotVar =
        new(@"\{\{\s*_\.(\w+)\s*\}\}", RegexOptions.Compiled);

    // Matches a full {% response ... %} tag including surrounding whitespace.
    // Groups: 1=attribute, 2=requestId, 3=pathOrB64, 4=frequency, 5=seconds (optional)
    private static readonly Regex InsomniaResponseTag =
        new(@"\{%\s*response\s+'([^']+)'\s*,\s*'([^']+)'\s*,\s*'([^']+)'\s*,\s*'([^']+)'\s*(?:,\s*(\d+))?\s*%\}",
            RegexOptions.Compiled | RegexOptions.Singleline);

    // Matches {% faker 'fieldName' %} — Insomnia fake data tags
    private static readonly Regex InsomniaFakerTag =
        new(@"\{%\s*faker\s+'([^']+)'\s*%\}", RegexOptions.Compiled);

    // Used to split a value into (before, tag, after) parts.
    private static readonly Regex InsomniaResponseTagSplit =
        new(@"(\{%.*?%\})", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Converts Insomnia-specific Nunjucks variable syntax to Callsmith <c>{{name}}</c>.
    /// Plain <c>{{name}}</c> references are already compatible and are left unchanged.
    /// Dynamic script expressions (<c>{% ... %}</c>) are left as literal strings.
    /// </summary>
    internal static string NormalizeVariables(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // {{ _['var-name'] }} → {{var-name}}
        var result = NunjucksIndexedVar.Replace(value, m => $"{{{{{m.Groups[1].Value}}}}}");

        // {{ _.varName }} → {{varName}}
        result = NunjucksDotVar.Replace(result, m => $"{{{{{m.Groups[1].Value}}}}}");

        return result;
    }

    /// <summary>
    /// Normalizes Nunjucks variable syntax AND converts Insomnia <c>{% response %}</c> /
    /// <c>{% faker %}</c> tags to the Callsmith inline format for direct use in request fields.
    /// </summary>
    internal static string NormalizeAndConvertTags(
        string? value, IReadOnlyDictionary<string, string> requestIdMap)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        // First normalize {{ }} syntax
        var result = NormalizeVariables(value);

        if (!result.Contains("{%", StringComparison.Ordinal)) return result;

        // Convert {% response ... %} — resolve requestId to display name, decode path
        result = InsomniaResponseTag.Replace(result, match =>
        {
            var requestId = match.Groups[2].Value;
            var pathRaw   = match.Groups[3].Value;
            var freq      = match.Groups[4].Value;
            var secs      = match.Groups[5].Value;

            var requestName = requestIdMap.TryGetValue(requestId, out var n) ? n : requestId;
            var path = DecodePath(pathRaw);

            return secs.Length > 0
                ? $"{{% response 'body', '{requestName}', '{path}', '{freq}', {secs} %}}"
                : $"{{% response 'body', '{requestName}', '{path}', '{freq}' %}}";
        });

        // Convert {% faker 'bogusName' %} → {% faker 'Category.Field' %}
        result = InsomniaFakerTag.Replace(result, match =>
        {
            var bogusName = match.Groups[1].Value;
            var entry = MockDataCatalog.FindByBogusName(bogusName);
            if (entry is not null)
                return $"{{% faker '{SegmentSerializer.MockDataKey(entry.Category, entry.Field)}' %}}";

            // Try dot-notation 'Category.Field' lookup directly
            var dotIdx = bogusName.IndexOf('.', StringComparison.Ordinal);
            if (dotIdx > 0)
            {
                var cat = bogusName[..dotIdx];
                var fld = bogusName[(dotIdx + 1)..];
                var direct = MockDataCatalog.All.FirstOrDefault(e =>
                    string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Field, fld, StringComparison.OrdinalIgnoreCase));
                if (direct is not null)
                    return $"{{% faker '{SegmentSerializer.MockDataKey(direct.Category, direct.Field)}' %}}";
            }

            // Unknown faker — keep as-is
            return match.Value;
        });

        return result;
    }

    /// <summary>
    /// Converts Insomnia <c>:paramName</c> path parameter syntax to
    /// Callsmith <c>{paramName}</c> syntax in a URL string.
    /// </summary>
    internal static string ConvertPathParamSyntax(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        return InsomniaPathParamSyntax.Replace(url, m => $"{{{m.Groups[1].Value}}}");
    }

    /// <summary>Returns true when the value contains an Insomnia template script expression.</summary>
    internal static bool IsScriptValue(string? value) =>
        value is not null && value.Contains("{%", StringComparison.Ordinal);

    /// <summary>
    /// Scans <paramref name="value"/> for <c>{% faker %}</c> and <c>{% response %}</c> tags.
    /// For each tag found: creates (or reuses) a named <see cref="ImportedDynamicVariable"/> in
    /// <paramref name="globalVars"/> and replaces the tag with a <c>{{var-name}}</c> reference.
    /// When <paramref name="globalVars"/> is null, any tags found are converted to Callsmith
    /// inline format via <see cref="NormalizeAndConvertTags"/> instead.
    /// </summary>
    internal static string ExtractTagsToGlobalVars(
        string value,
        IReadOnlyDictionary<string, string> requestIdMap,
        Dictionary<string, ImportedDynamicVariable>? globalVars)
    {
        if (!value.Contains("{%", StringComparison.Ordinal)) return value;
        if (globalVars is null) return NormalizeAndConvertTags(value, requestIdMap);

        return InsomniaResponseTagSplit.Replace(value, match =>
        {
            var tag = match.Groups[1].Value;

            // {% faker %} tag
            var fakerMatch = InsomniaFakerTag.Match(tag);
            if (fakerMatch.Success)
            {
                var (cat, fld) = ResolveFakerEntry(fakerMatch.Groups[1].Value);
                // Derive a deterministic var name: "faker-category-field" lowercased, hyphens
                var varName = $"faker-{cat}-{fld}"
                    .ToLowerInvariant()
                    .Replace(' ', '-');

                if (!globalVars.ContainsKey(varName))
                {
                    globalVars[varName] = new ImportedDynamicVariable
                    {
                        Name = varName,
                        MockDataCategory = cat,
                        MockDataField = fld,
                    };
                }
                return $"{{{{{varName}}}}}";
            }

            // {% response %} tag
            var responseMatch = InsomniaResponseTag.Match(tag);
            if (responseMatch.Success)
            {
                var requestId = responseMatch.Groups[2].Value;
                var pathRaw = responseMatch.Groups[3].Value;
                var freq = responseMatch.Groups[4].Value;
                var secs = responseMatch.Groups[5].Value;

                var requestName = requestIdMap.TryGetValue(requestId, out var n) ? n : requestId;
                var path = DecodePath(pathRaw);
                var frequency = ParseFrequency(freq);
                int? expiresAfterSeconds = int.TryParse(secs, out var s) ? s : null;

                // Derive a deterministic var name from request name + json-path leaf
                var pathLeaf = path.Split(['.', '$', '[', ']'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "value";
                var baseName = $"{pathLeaf}"
                    .ToLowerInvariant()
                    .Replace('_', '-')
                    .Trim('-');
                var varName = baseName;
                var counter = 2;
                // Ensure name is unique and not already registered for a *different* request+path.
                while (globalVars.TryGetValue(varName, out var existing)
                    && (existing.ResponseRequestName != requestName || existing.ResponsePath != path))
                {
                    varName = $"{baseName}-{counter++}";
                }

                if (!globalVars.ContainsKey(varName))
                {
                    globalVars[varName] = new ImportedDynamicVariable
                    {
                        Name = varName,
                        ResponseRequestName = requestName,
                        ResponsePath = path,
                        ResponseFrequency = frequency,
                        ResponseExpiresAfterSeconds = expiresAfterSeconds,
                    };
                }
                return $"{{{{{varName}}}}}";
            }

            // Unknown tag — leave as-is
            return tag;
        });
    }

    /// <summary>Resolves a faker bogus-name or dot-notation key to a (category, field) pair.</summary>
    private static (string category, string field) ResolveFakerEntry(string bogusName)
    {
        var entry = MockDataCatalog.FindByBogusName(bogusName);
        if (entry is not null) return (entry.Category, entry.Field);

        var dotIdx = bogusName.IndexOf('.', StringComparison.Ordinal);
        if (dotIdx > 0)
        {
            var cat = bogusName[..dotIdx];
            var fld = bogusName[(dotIdx + 1)..];
            var direct = MockDataCatalog.All.FirstOrDefault(e =>
                string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Field, fld, StringComparison.OrdinalIgnoreCase));
            if (direct is not null) return (direct.Category, direct.Field);
            return (cat, fld);
        }

        return ("Random", "UUID");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dynamic variable parsing (for environment variables)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses an Insomnia value that contains one or more <c>{% response ... %}</c> or
    /// <c>{% faker ... %}</c> tags into a list of <see cref="ValueSegment"/>s.
    /// Returns an empty list when parsing fails.
    /// </summary>
    internal static IReadOnlyList<ValueSegment> ParseDynamicValue(
        string value, IReadOnlyDictionary<string, string> requestIdMap)
    {
        if (string.IsNullOrEmpty(value)) return [];

        var parts = InsomniaResponseTagSplit.Split(value);
        var segments = new List<ValueSegment>();

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            // {% faker %} tag
            var fakerMatch = InsomniaFakerTag.Match(part);
            if (fakerMatch.Success)
            {
                var bogusName = fakerMatch.Groups[1].Value;
                var entry = MockDataCatalog.FindByBogusName(bogusName);
                string cat, fld;
                if (entry is not null)
                {
                    cat = entry.Category; fld = entry.Field;
                }
                else
                {
                    var dotIdx = bogusName.IndexOf('.', StringComparison.Ordinal);
                    if (dotIdx > 0)
                    {
                        cat = bogusName[..dotIdx]; fld = bogusName[(dotIdx + 1)..];
                        var direct = MockDataCatalog.All.FirstOrDefault(e =>
                            string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(e.Field, fld, StringComparison.OrdinalIgnoreCase));
                        if (direct is not null) { cat = direct.Category; fld = direct.Field; }
                    }
                    else { cat = "Random"; fld = "UUID"; }
                }
                segments.Add(new MockDataSegment { Category = cat, Field = fld });
                continue;
            }

            // {% response %} tag
            var tagMatch = InsomniaResponseTag.Match(part);
            if (!tagMatch.Success)
            {
                // Static text (may contain Nunjucks variable refs — normalise them)
                var text = NormalizeVariables(part);
                if (!string.IsNullOrEmpty(text))
                    segments.Add(new StaticValueSegment { Text = text });
                continue;
            }

            // attribute: 'body', 'header', 'status' — we currently only support 'body'
            // var attribute = tagMatch.Groups[1].Value;

            var requestId = tagMatch.Groups[2].Value;
            var pathRaw = tagMatch.Groups[3].Value;
            var frequencyRaw = tagMatch.Groups[4].Value;
            var secondsRaw = tagMatch.Groups[5].Value;

            var requestName = requestIdMap.TryGetValue(requestId, out var name) ? name : requestId;
            var path = DecodePath(pathRaw);
            var frequency = ParseFrequency(frequencyRaw);
            int? expiresAfterSeconds = int.TryParse(secondsRaw, out var secs) ? secs : null;

            segments.Add(new DynamicValueSegment
            {
                RequestName = requestName,
                Path = path,
                Frequency = frequency,
                ExpiresAfterSeconds = expiresAfterSeconds,
            });
        }

        return segments;
    }

    /// <summary>
    /// Decodes an Insomnia path expression, which may be base64-encoded in the format
    /// <c>b64::BASE64DATA::46b</c>. Plain strings are returned unchanged.
    /// </summary>
    private static string DecodePath(string pathRaw)
    {
        const string B64Prefix = "b64::";
        if (!pathRaw.StartsWith(B64Prefix, StringComparison.Ordinal))
            return pathRaw;

        // b64::BASE64::46b  →  strip prefix and the trailing ::... checksum
        var withoutPrefix = pathRaw[B64Prefix.Length..];
        var separatorIndex = withoutPrefix.LastIndexOf("::", StringComparison.Ordinal);
        var b64 = separatorIndex >= 0 ? withoutPrefix[..separatorIndex] : withoutPrefix;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return pathRaw; // fallback: return raw if base64 decode fails
        }
    }

    private static DynamicFrequency ParseFrequency(string raw) =>
        raw switch
        {
            "always" => DynamicFrequency.Always,
            "when-expired" => DynamicFrequency.IfExpired,
            "never" => DynamicFrequency.Never,
            _ => DynamicFrequency.Always,
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Request ID → name mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recursively walks the collection item tree and builds a map of
    /// <c>requestId → display path</c> (e.g. <c>"req_abc" → "Auth/Get Token"</c>).
    /// </summary>
    private static Dictionary<string, string> BuildRequestIdMap(
        List<InsomniaCollectionItem> items, string prefix = "")
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.IsRequest)
            {
                var requestPath = string.IsNullOrEmpty(prefix)
                    ? item.Name
                    : $"{prefix}/{item.Name}";

                if (!string.IsNullOrWhiteSpace(item.Meta?.Id))
                    map[item.Meta.Id] = requestPath;
            }
            else if (item.Children is { Count: > 0 })
            {
                var folderPrefix = string.IsNullOrEmpty(prefix)
                    ? item.Name
                    : $"{prefix}/{item.Name}";
                foreach (var kv in BuildRequestIdMap(item.Children, folderPrefix))
                    map[kv.Key] = kv.Value;
            }
        }
        return map;
    }
}
