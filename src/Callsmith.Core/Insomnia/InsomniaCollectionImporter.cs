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
using ApiKeyLocations = Callsmith.Core.Models.AuthConfig.ApiKeyLocations;
using AuthTypes = Callsmith.Core.Models.AuthConfig.AuthTypes;
using BodyTypes = Callsmith.Core.Models.CollectionRequest.BodyTypes;

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
            Name = string.IsNullOrWhiteSpace(item.Name) ? ImporterConstants.UnnamedRequest : item.Name,
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
            Name = string.IsNullOrWhiteSpace(item.Name) ? ImporterConstants.UnnamedFolder : item.Name,
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

            // Try pure mock-data tag (faker/uuid/timestamp)
            if (TryResolveMockDataTag(kv.Value.Trim(), out var mdCategory, out var mdField))
            {
                dynamicVars.Add(new ImportedDynamicVariable
                {
                    Name = kv.Key,
                    MockDataCategory = mdCategory,
                    MockDataField = mdField,
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
            _ => new AuthConfig { AuthType = AuthTypes.None },
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
            BodyTypes.JsonContentType => BodyTypes.Json,
            BodyTypes.TextContentType => BodyTypes.Text,
            BodyTypes.XmlContentType or "text/xml" => BodyTypes.Xml,
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

    // Matches {% uuid %} / {% uuid 'v4' %} style tags.
    private static readonly Regex InsomniaUuidTag =
        new(@"\{%\s*uuid(?:\s+'[^']*')?\s*%\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches timestamp-like tags (e.g. {% timestamp %}, {% now 'iso-8601' %}).
    private static readonly Regex InsomniaTimestampTag =
        new(@"\{%\s*(?:timestamp|now)(?:\s+[^%]+)?\s*%\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Used to split a value into (before, tag, after) parts.
    private static readonly Regex InsomniaResponseTagSplit =
        new(@"(\{%.*?%\})", RegexOptions.Compiled | RegexOptions.Singleline);

    private static bool TryResolveMockDataTag(string tag, out string category, out string field)
    {
        category = string.Empty;
        field = string.Empty;

        var fakerMatch = InsomniaFakerTag.Match(tag);
        if (fakerMatch.Success)
        {
            (category, field) = ResolveFakerEntry(fakerMatch.Groups[1].Value);
            return true;
        }

        if (InsomniaUuidTag.IsMatch(tag))
        {
            category = "Random";
            field = "UUID";
            return true;
        }

        if (InsomniaTimestampTag.IsMatch(tag))
        {
            // Prefer ISO when explicitly requested; otherwise map to Unix timestamp.
            if (tag.Contains("iso", StringComparison.OrdinalIgnoreCase))
            {
                category = "Date";
                field = "ISO Timestamp";
            }
            else
            {
                category = "Date";
                field = "Timestamp";
            }

            return true;
        }

        return false;
    }

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
            // ResolveFakerEntry handles alias map + dot-notation + randomXyz heuristic
            var (cat, fld) = ResolveFakerEntry(bogusName);
            // If the resolved entry exists in the catalog, emit the canonical key
            if (MockDataCatalog.All.Any(e => e.Category == cat && e.Field == fld))
                return $"{{% faker '{SegmentSerializer.MockDataKey(cat, fld)}' %}}";

            // Unknown faker — keep as-is
            return match.Value;
        });

        // Convert UUID tag to equivalent mock-data key.
        result = InsomniaUuidTag.Replace(result,
            $"{{% faker '{SegmentSerializer.MockDataKey("Random", "UUID")}' %}}");

        // Convert timestamp-like tags to Date.Timestamp / Date.ISO Timestamp.
        result = InsomniaTimestampTag.Replace(result, m =>
            m.Value.Contains("iso", StringComparison.OrdinalIgnoreCase)
                ? $"{{% faker '{SegmentSerializer.MockDataKey("Date", "ISO Timestamp")}' %}}"
                : $"{{% faker '{SegmentSerializer.MockDataKey("Date", "Timestamp")}' %}}");

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

            // Mock-data tag (faker / uuid / timestamp)
            if (TryResolveMockDataTag(tag, out var mdCategory, out var mdField))
            {
                // Derive a deterministic var name using Postman-style naming.
                var varName = $"{mdCategory}-{mdField}"
                    .ToLowerInvariant()
                    .Replace(' ', '-');

                if (!globalVars.ContainsKey(varName))
                {
                    globalVars[varName] = new ImportedDynamicVariable
                    {
                        Name = varName,
                        MockDataCategory = mdCategory,
                        MockDataField = mdField,
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
        var entry = FindByInsomniaBogusName(bogusName);
        if (entry is not null) return (entry.Category, entry.Field);

        // Dot-notation pass-through for unknown keys (e.g. a raw "Category.Field" not in the alias map)
        var dotIdx = bogusName.IndexOf('.', StringComparison.Ordinal);
        if (dotIdx > 0)
            return (bogusName[..dotIdx], bogusName[(dotIdx + 1)..]);

        return ("Random", "UUID");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Insomnia faker-name → MockDataCatalog resolution
    // Insomnia uses Faker.js naming conventions (randomFirstName, guid, etc.).
    // This mapping is Insomnia-specific and does not belong in MockDataCatalog.
    // ─────────────────────────────────────────────────────────────────────────

    private static MockDataEntry? FindByInsomniaBogusName(string bogusName)
    {
        if (string.IsNullOrEmpty(bogusName)) return null;

        var key = bogusName.ToLowerInvariant();

        if (InsomniaBogusNameMap.TryGetValue(key, out var entry))
            return entry;

        // Dot-notation: e.g. "internet.ipv6" → category-scoped fuzzy field match
        var dotIdx = key.IndexOf('.', StringComparison.Ordinal);
        if (dotIdx > 0)
        {
            var categoryPart  = key[..dotIdx];
            var normalizedFld = NormalizeInsomniaBogusKey(key[(dotIdx + 1)..]);
            return FindBestInsomniaCatalogMatch(
                MockDataCatalog.All.Where(e => string.Equals(e.Category, categoryPart, StringComparison.OrdinalIgnoreCase)),
                normalizedFld);
        }

        // randomXyz heuristic: strip "random" prefix, fuzzy-match against all field names
        if (key.StartsWith("random", StringComparison.Ordinal) && key.Length > "random".Length)
        {
            var tail = NormalizeInsomniaBogusKey(key["random".Length..]);
            return FindBestInsomniaCatalogMatch(MockDataCatalog.All, tail);
        }

        return null;
    }

    private static MockDataEntry? FindBestInsomniaCatalogMatch(IEnumerable<MockDataEntry> entries, string normalizedCandidate)
    {
        return entries
            .Select(e => (Entry: e, Score: InsomniaCatalogFieldScore(normalizedCandidate, e.Field)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => NormalizeInsomniaBogusKey(x.Entry.Field).Length)
            .Select(x => x.Entry)
            .FirstOrDefault();
    }

    private static int InsomniaCatalogFieldScore(string normalizedCandidate, string field)
    {
        var nf = NormalizeInsomniaBogusKey(field);
        if (nf == normalizedCandidate)                                    return 300;
        if (nf.StartsWith(normalizedCandidate, StringComparison.Ordinal)) return 200;
        if (normalizedCandidate.StartsWith(nf, StringComparison.Ordinal)) return 100;
        return 0;
    }

    private static string NormalizeInsomniaBogusKey(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    // Alias table: Insomnia / Faker.js method name (lowercased) → MockDataCatalog entry.
    // Source: https://github.com/Kong/insomnia/blob/main/packages/insomnia/src/templating/faker-functions.ts
    private static readonly Dictionary<string, MockDataEntry> InsomniaBogusNameMap = BuildInsomniaBogusNameMap();

    private static Dictionary<string, MockDataEntry> BuildInsomniaBogusNameMap()
    {
        var firstName    = MockDataCatalog.All.First(e => e.Category == "Name"     && e.Field == "First Name");
        var lastName     = MockDataCatalog.All.First(e => e.Category == "Name"     && e.Field == "Last Name");
        var fullName     = MockDataCatalog.All.First(e => e.Category == "Name"     && e.Field == "Full Name");
        var prefix       = MockDataCatalog.All.First(e => e.Category == "Name"     && e.Field == "Prefix");
        var suffix       = MockDataCatalog.All.First(e => e.Category == "Name"     && e.Field == "Suffix");
        var jobTitle     = MockDataCatalog.All.First(e => e.Category == "Name"     && e.Field == "Job Title");
        var email        = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Email");
        var exampleEmail = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Example Email");
        var username     = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Username");
        var url          = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "URL");
        var password     = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Password");
        var ipAddress    = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "IP Address");
        var color        = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Color");
        var avatarUrl    = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Avatar URL");
        var imageUrl     = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Image URL");
        var domainName   = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Domain Name");
        var phone        = MockDataCatalog.All.First(e => e.Category == "Phone"    && e.Field == "Phone Number");
        var city         = MockDataCatalog.All.First(e => e.Category == "Address"  && e.Field == "City");
        var state        = MockDataCatalog.All.First(e => e.Category == "Address"  && e.Field == "State");
        var country      = MockDataCatalog.All.First(e => e.Category == "Address"  && e.Field == "Country");
        var zipCode      = MockDataCatalog.All.First(e => e.Category == "Address"  && e.Field == "Zip Code");
        var word         = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Word");
        var words        = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Words");
        var sentence     = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Sentence");
        var sentences    = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Sentences");
        var paragraph    = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Paragraph");
        var paragraphs   = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Paragraphs");
        var loremText    = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Text");
        var loremLines   = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Lines");
        var loremSlug    = MockDataCatalog.All.First(e => e.Category == "Lorem"    && e.Field == "Slug");
        var uuid         = MockDataCatalog.All.First(e => e.Category == "Random"   && e.Field == "UUID");
        var number       = MockDataCatalog.All.First(e => e.Category == "Random"   && e.Field == "Number");
        var boolean      = MockDataCatalog.All.First(e => e.Category == "Random"   && e.Field == "Boolean");
        var pastDate     = MockDataCatalog.All.First(e => e.Category == "Date"     && e.Field == "Past Date");
        var futureDate   = MockDataCatalog.All.First(e => e.Category == "Date"     && e.Field == "Future Date");
        var recentDate   = MockDataCatalog.All.First(e => e.Category == "Date"     && e.Field == "Recent Date");
        var compName     = MockDataCatalog.All.First(e => e.Category == "Company"  && e.Field == "Company Name");
        var buzzwords    = MockDataCatalog.All.First(e => e.Category == "Company"  && e.Field == "Buzzwords");
        var hackerAdj    = MockDataCatalog.All.First(e => e.Category == "Hacker"   && e.Field == "Adjective");
        var hackerNoun   = MockDataCatalog.All.First(e => e.Category == "Hacker"   && e.Field == "Noun");
        var hackerVerb   = MockDataCatalog.All.First(e => e.Category == "Hacker"   && e.Field == "Verb");
        var creditCard   = MockDataCatalog.All.First(e => e.Category == "Finance"  && e.Field == "Credit Card");
        var iban         = MockDataCatalog.All.First(e => e.Category == "Finance"  && e.Field == "IBAN");
        var bic          = MockDataCatalog.All.First(e => e.Category == "Finance"  && e.Field == "BIC");
        var accountNum   = MockDataCatalog.All.First(e => e.Category == "Finance"  && e.Field == "Account Number");
        var dbColumn     = MockDataCatalog.All.First(e => e.Category == "Database" && e.Field == "Column");
        var dbType       = MockDataCatalog.All.First(e => e.Category == "Database" && e.Field == "Type");
        var dbCollation  = MockDataCatalog.All.First(e => e.Category == "Database" && e.Field == "Collation");
        var dbEngine     = MockDataCatalog.All.First(e => e.Category == "Database" && e.Field == "Engine");

        return new Dictionary<string, MockDataEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // Name
            ["randomfirstname"]        = firstName,
            ["name.firstname"]         = firstName,
            ["firstname"]              = firstName,
            ["randomlastname"]         = lastName,
            ["name.lastname"]          = lastName,
            ["lastname"]               = lastName,
            ["fullname"]               = fullName,
            ["name.fullname"]          = fullName,
            ["randomfullname"]         = fullName,
            ["randomnameprefix"]       = prefix,
            ["randomnamesuffix"]       = suffix,
            ["jobtitle"]               = jobTitle,
            ["name.jobtitle"]          = jobTitle,
            // Internet
            ["internet.email"]         = email,
            ["randomemail"]            = email,
            ["email"]                  = email,
            ["internet.exampleemail"]  = exampleEmail,
            ["randomexampleemail"]     = exampleEmail,
            ["exampleemail"]           = exampleEmail,
            ["internet.username"]      = username,
            ["randomusername"]         = username,
            ["username"]               = username,
            ["internet.url"]           = url,
            ["randomurl"]              = url,
            ["url"]                    = url,
            ["internet.password"]      = password,
            ["randompassword"]         = password,
            ["internet.ip"]            = ipAddress,
            ["randomip"]               = ipAddress,
            ["ipaddress"]              = ipAddress,
            ["internet.domainname"]    = domainName,
            ["randomdomainname"]       = domainName,
            ["randomhexcolor"]         = color,
            ["randomavatarimage"]      = avatarUrl,
            // Flickr-category image variants all map to the generic image URL entry
            ["randomabstractimage"]    = imageUrl,
            ["randomanimalsimage"]     = imageUrl,
            ["randombusinessimage"]    = imageUrl,
            ["randomcatsimage"]        = imageUrl,
            ["randomcityimage"]        = imageUrl,
            ["randomfoodimage"]        = imageUrl,
            ["randomnightlifeimage"]   = imageUrl,
            ["randomfashionimage"]     = imageUrl,
            ["randompeopleimage"]      = imageUrl,
            ["randomnatureimage"]      = imageUrl,
            ["randomsportsimage"]      = imageUrl,
            ["randomtransportimage"]   = imageUrl,
            ["randomimagedatauri"]     = imageUrl,
            // Phone
            ["phone.phonenumber"]      = phone,
            ["randomphonenumber"]      = phone,
            ["phonenumber"]            = phone,
            // Address
            ["address.city"]           = city,
            ["randomcity"]             = city,
            ["city"]                   = city,
            ["address.state"]          = state,
            ["randomstate"]            = state,
            ["state"]                  = state,
            ["address.country"]        = country,
            ["randomcountry"]          = country,
            ["country"]                = country,
            ["address.zipcode"]        = zipCode,
            ["randomzipcode"]          = zipCode,
            ["zipcode"]                = zipCode,
            // Lorem
            ["lorem.word"]             = word,
            ["randomword"]             = word,
            ["randomloremword"]        = word,
            ["lorem.words"]            = words,
            ["randomloremwords"]       = words,
            ["lorem.sentence"]         = sentence,
            ["randomsentence"]         = sentence,
            ["randomloremsentence"]    = sentence,
            ["lorem.sentences"]        = sentences,
            ["randomloremsentences"]   = sentences,
            ["lorem.paragraph"]        = paragraph,
            ["randomparagraph"]        = paragraph,
            ["randomloremparagraph"]   = paragraph,
            ["lorem.paragraphs"]       = paragraphs,
            ["randomloremparagraphs"]  = paragraphs,
            ["lorem.text"]             = loremText,
            ["randomloremtext"]        = loremText,
            ["lorem.lines"]            = loremLines,
            ["randomloremlines"]       = loremLines,
            ["lorem.slug"]             = loremSlug,
            ["randomloremslug"]        = loremSlug,
            // Random
            ["random.uuid"]            = uuid,
            ["randomuuid"]             = uuid,
            ["guid"]                   = uuid,
            ["uuid"]                   = uuid,
            ["random.number"]          = number,
            ["randomnumber"]           = number,
            ["randomint"]              = number,
            ["random.boolean"]         = boolean,
            ["randomboolean"]          = boolean,
            // Date
            ["date.recent"]            = recentDate,
            ["randomdaterecent"]       = recentDate,
            ["randomdatepast"]         = pastDate,
            ["date.future"]            = futureDate,
            ["randomdatefuture"]       = futureDate,
            // Company
            ["company.companyname"]    = compName,
            ["randomcompanyname"]      = compName,
            ["companyname"]            = compName,
            ["randomcompanysuffix"]    = compName,
            ["randombs"]               = buzzwords,
            ["randombsadjective"]      = hackerAdj,
            ["randombsbuzz"]           = hackerVerb,
            ["randombsnoun"]           = hackerNoun,
            // Finance
            ["finance.creditcardnumber"] = creditCard,
            ["creditcardnumber"]       = creditCard,
            ["finance.iban"]           = iban,
            ["randombankaccountiban"]  = iban,
            ["finance.bic"]            = bic,
            ["randombankaccountbic"]   = bic,
            ["randombankaccount"]      = accountNum,
            // Database
            ["randomdatabasecolumn"]   = dbColumn,
            ["randomdatabasetype"]     = dbType,
            ["randomdatabasecollation"]= dbCollation,
            ["randomdatabaseengine"]   = dbEngine,
        };
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

            // Mock-data tag (faker / uuid / timestamp)
            if (TryResolveMockDataTag(part, out var mdCategory, out var mdField))
            {
                segments.Add(new MockDataSegment { Category = mdCategory, Field = mdField });
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
