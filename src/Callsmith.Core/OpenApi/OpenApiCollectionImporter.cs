using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Import;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using BodyTypes = Callsmith.Core.Models.CollectionRequest.BodyTypes;

namespace Callsmith.Core.OpenApi;

/// <summary>
/// Imports OpenAPI 3.x and Swagger 2.x specs (JSON or YAML) into the
/// Callsmith <see cref="ImportedCollection"/> domain model.
/// </summary>
/// <remarks>
/// Each API operation becomes one request. Operations are grouped into folders
/// by their first tag; untagged operations land at the root. Each server entry
/// (OAS3 <c>servers[]</c> or the Swagger 2 host/basePath combination) becomes
/// an environment whose single variable <c>baseUrl</c> holds the server URL.
/// All generated request URLs are of the form <c>{{baseUrl}}/path</c>.
/// </remarks>
public sealed partial class OpenApiCollectionImporter : ICollectionImporter
{
    /// <summary>
    /// The environment variable name written into every imported environment whose value
    /// holds the server base URL. Request URLs are generated as <c>{{baseUrl}}/path</c>.
    /// Changing this value would invalidate all collections previously imported by this importer.
    /// </summary>
    private const string BaseUrlVar = "baseUrl";

    /// <summary>The display name shown in the import-type selector.</summary>
    public const string DisplayName = "Open API 3.x / Swagger 2.0";

    // Standard HTTP method keys present in a Path Item object.
    private static readonly IReadOnlyList<string> HttpMethods =
        ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

    private static readonly JsonDocumentOptions JsonDocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlToJsonSerializer = new SerializerBuilder()
        .JsonCompatible()
        .Build();

    // Matches `{paramName}` path segments (single-brace, not double-brace env vars).
    [GeneratedRegex(@"\{([A-Za-z0-9_\-\.]+)\}", RegexOptions.Compiled)]
    private static partial Regex PathParamRegex();

    private readonly ILogger<OpenApiCollectionImporter> _logger;

    /// <summary>
    /// One-shot cache populated by <see cref="CanImportAsync"/> and consumed by
    /// <see cref="ImportAsync"/> so that the file is read and parsed only once when
    /// the two methods are called in immediate succession on the same path (the normal
    /// import flow). Falls back to a fresh disk read if the paths do not match.
    /// Written and read with <see cref="Interlocked.Exchange{T}"/> for thread safety.
    /// </summary>
    private CachedParse? _lastValidatedParse;

    /// <summary>Holds the raw file content from a successful <see cref="CanImportAsync"/> call.</summary>
    private sealed record CachedParse(string FilePath, string Content);

    public OpenApiCollectionImporter(ILogger<OpenApiCollectionImporter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string FormatName => DisplayName;

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedFileExtensions { get; } = [".json", ".yaml", ".yml"];

    /// <inheritdoc/>
    public async Task<bool> CanImportAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);

            JsonDocument doc;
            try
            {
                doc = ParseContent(content);
            }
            catch
            {
                // File is not valid JSON or YAML — cannot be an OpenAPI/Swagger spec.
                Interlocked.Exchange(ref _lastValidatedParse, null);
                return false;
            }

            using (doc)
            {
                if (!IsSupportedSpec(doc.RootElement))
                {
                    Interlocked.Exchange(ref _lastValidatedParse, null);
                    return false;
                }
            }

            // Cache the raw content so ImportAsync can skip the second disk read.
            Interlocked.Exchange(ref _lastValidatedParse, new CachedParse(filePath, content));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CanImportAsync: could not read '{FilePath}'", filePath);
            Interlocked.Exchange(ref _lastValidatedParse, null);
            return false;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the document root element contains an
    /// <c>openapi</c> key whose value starts with <c>"3."</c> (OpenAPI 3.x) or
    /// a <c>swagger</c> key whose value starts with <c>"2."</c> (Swagger 2.x).
    /// Returns <see langword="false"/> for any other content, including unsupported
    /// versions (e.g. OpenAPI 4, Swagger 1).
    /// </summary>
    private static bool IsSupportedSpec(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        // OpenAPI 3.x
        if (root.TryGetProperty("openapi", out var oasEl))
        {
            var version = oasEl.ValueKind == JsonValueKind.String ? oasEl.GetString() : null;
            return version is not null && version.StartsWith("3.", StringComparison.Ordinal);
        }

        // Swagger 2.x
        if (root.TryGetProperty("swagger", out var swEl))
        {
            var version = swEl.ValueKind == JsonValueKind.String ? swEl.GetString() : null;
            return version is not null && version.StartsWith("2.", StringComparison.Ordinal);
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<ImportedCollection> ImportAsync(
        string filePath, CancellationToken ct = default)
    {
        // Consume the one-shot cache populated by CanImportAsync (same file path) to avoid
        // reading and parsing the file a second time in the normal import flow.
        var cached = Interlocked.Exchange(ref _lastValidatedParse, null);
        var content = (cached?.FilePath == filePath)
            ? cached.Content
            : await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);

        JsonDocument doc;
        try
        {
            doc = ParseContent(content);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse OpenAPI/Swagger spec from '{filePath}'.", ex);
        }

        using (doc)
        {
            return BuildCollection(doc.RootElement);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Content parsing: JSON or YAML → JsonDocument
    // ─────────────────────────────────────────────────────────────────────────

    private static JsonDocument ParseContent(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            // Plain JSON
            return JsonDocument.Parse(content, JsonDocOptions);
        }

        // YAML → generic object tree → JSON string → JsonDocument
        var yamlObj = YamlDeserializer.Deserialize<object?>(content);
        if (yamlObj is null)
            throw new InvalidOperationException("YAML document is empty or null.");

        var json = YamlToJsonSerializer.Serialize(yamlObj);
        return JsonDocument.Parse(json, JsonDocOptions);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Collection builder
    // ─────────────────────────────────────────────────────────────────────────

    private ImportedCollection BuildCollection(JsonElement root)
    {
        var collectionName = GetString(root, "info", "title")
            ?? GetString(root, "info", "description")
            ?? "Imported Collection";

        if (string.IsNullOrWhiteSpace(collectionName))
            collectionName = "Imported Collection";

        var isOas3 = root.TryGetProperty("openapi", out _);
        var environments = isOas3
            ? ExtractOas3Environments(root)
            : ExtractSwagger2Environments(root);

        var paths = root.TryGetProperty("paths", out var pathsEl)
            ? pathsEl
            : default;

        var (rootRequests, rootFolders, rootOrder) = ExtractOperations(root, paths);

        _logger.LogInformation(
            "Parsed {Format} spec '{Name}': {Ops} root operations, {Folders} tag folders, {Envs} environments",
            isOas3 ? "OpenAPI 3.x" : "Swagger 2.x",
            collectionName,
            rootRequests.Count,
            rootFolders.Count,
            environments.Count);

        return new ImportedCollection
        {
            Name = collectionName,
            RootRequests = rootRequests,
            RootFolders = rootFolders,
            ItemOrder = rootOrder,
            Environments = environments,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Environment extraction
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ImportedEnvironment> ExtractOas3Environments(JsonElement root)
    {
        if (!root.TryGetProperty("servers", out var serversEl)
            || serversEl.ValueKind != JsonValueKind.Array)
        {
            // No servers defined — create a placeholder environment.
            return [MakeEnvironment("Default", string.Empty)];
        }

        var envs = new List<ImportedEnvironment>();
        var index = 0;
        foreach (var server in serversEl.EnumerateArray())
        {
            var url = GetString(server, "url") ?? string.Empty;
            var description = GetString(server, "description");

            // Build a meaningful environment name.
            var name = !string.IsNullOrWhiteSpace(description)
                ? description
                : envs.Count == 0
                    ? "Default"
                    : $"Server {index + 1}";

            // Resolve any OAS3 server URL variables (e.g. {scheme}, {host}).
            url = ResolveServerVariables(url, server);

            envs.Add(MakeEnvironment(name, url));
            index++;
        }

        return envs;
    }

    private static IReadOnlyList<ImportedEnvironment> ExtractSwagger2Environments(JsonElement root)
    {
        var host = GetString(root, "host") ?? string.Empty;
        var basePath = GetString(root, "basePath") ?? string.Empty;

        // Schemes: prefer https, fall back to http, then whatever is listed.
        var scheme = "https";
        if (root.TryGetProperty("schemes", out var schemesEl)
            && schemesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in schemesEl.EnumerateArray())
            {
                var sv = s.GetString()?.ToLowerInvariant();
                if (sv == "https") { scheme = "https"; break; }
                if (sv == "http")  { scheme = "http"; }
            }
        }

        var url = string.IsNullOrWhiteSpace(host)
            ? string.Empty
            : $"{scheme}://{host.TrimEnd('/')}{basePath}";

        return [MakeEnvironment("Default", url)];
    }

    private static ImportedEnvironment MakeEnvironment(string name, string serverUrl)
        => new()
        {
            Name = name,
            Variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BaseUrlVar] = serverUrl,
            },
        };

    /// <summary>
    /// Substitutes OAS3 server-URL template variables with their defaults,
    /// e.g. <c>{scheme}://api.example.com</c> → <c>https://api.example.com</c>.
    /// </summary>
    private static string ResolveServerVariables(string url, JsonElement server)
    {
        if (!url.Contains('{', StringComparison.Ordinal))
            return url;

        if (!server.TryGetProperty("variables", out var varsEl)
            || varsEl.ValueKind != JsonValueKind.Object)
        {
            return url;
        }

        foreach (var prop in varsEl.EnumerateObject())
        {
            var defaultVal = GetString(prop.Value, "default") ?? string.Empty;
            url = url.Replace($"{{{prop.Name}}}", defaultVal, StringComparison.Ordinal);
        }

        return url;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Path / operation extraction
    // ─────────────────────────────────────────────────────────────────────────

    private (
        IReadOnlyList<ImportedRequest> rootRequests,
        IReadOnlyList<ImportedFolder>  rootFolders,
        IReadOnlyList<string>          rootOrder)
    ExtractOperations(JsonElement root, JsonElement paths)
    {
        if (paths.ValueKind != JsonValueKind.Object)
            return ([], [], []);

        // tag name → (requests, order)
        var tagGroups = new Dictionary<string, (List<ImportedRequest> Reqs, List<string> Order)>(
            StringComparer.OrdinalIgnoreCase);

        // Collect tag display order from the top-level `tags` array when present.
        var tagOrder = new List<string>();
        if (root.TryGetProperty("tags", out var tagsArr)
            && tagsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tagsArr.EnumerateArray())
            {
                var tn = GetString(t, "name");
                if (!string.IsNullOrWhiteSpace(tn))
                    tagOrder.Add(tn);
            }
        }

        var rootRequests = new List<ImportedRequest>();
        var rootOrder    = new List<string>();

        foreach (var pathProp in paths.EnumerateObject())
        {
            var pathTemplate = pathProp.Name;   // e.g. "/users/{id}"
            var pathItem     = pathProp.Value;

            // Path-level parameters (shared by all operations on this path).
            var pathLevelParams = pathItem.TryGetProperty("parameters", out var plp)
                ? plp
                : default;

            foreach (var method in HttpMethods)
            {
                if (!pathItem.TryGetProperty(method, out var opEl)
                    || opEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var req = BuildRequest(method, pathTemplate, opEl, pathLevelParams, root);

                var firstTag = GetFirstTag(opEl);
                if (firstTag is null)
                {
                    rootRequests.Add(req);
                    rootOrder.Add(req.Name);
                }
                else
                {
                    if (!tagGroups.TryGetValue(firstTag, out var group))
                    {
                        group = ([], []);
                        tagGroups[firstTag] = group;
                        // Add to tagOrder if not already known (operations may reference
                        // tags that aren't in the top-level tags array).
                        if (!tagOrder.Contains(firstTag, StringComparer.OrdinalIgnoreCase))
                            tagOrder.Add(firstTag);
                    }
                    group.Reqs.Add(req);
                    group.Order.Add(req.Name);
                }
            }
        }

        // Build folders in tag-order
        var rootFolders = new List<ImportedFolder>();
        foreach (var tag in tagOrder)
        {
            if (!tagGroups.TryGetValue(tag, out var group)) continue;
            rootFolders.Add(new ImportedFolder
            {
                Name      = tag,
                Requests  = group.Reqs,
                ItemOrder = group.Order,
            });
            rootOrder.Add(tag);
        }

        return (rootRequests, rootFolders, rootOrder);
    }

    private ImportedRequest BuildRequest(
        string method,
        string pathTemplate,
        JsonElement op,
        JsonElement pathLevelParams,
        JsonElement root)
    {
        var methodUpper = method.ToUpperInvariant();
        var operationId = GetString(op, "operationId");
        var summary     = GetString(op, "summary");
        var description = GetString(op, "description") ?? summary;

        // Request name: operationId > summary > "METHOD /path"
        var name = !string.IsNullOrWhiteSpace(operationId) ? operationId
                 : !string.IsNullOrWhiteSpace(summary)     ? summary
                 : $"{methodUpper} {pathTemplate}";

        // Merge path-level and operation-level parameters.
        // Operation-level parameters override path-level parameters with the same name+in.
        var mergedParams = MergeParameters(pathLevelParams, op);

        // URL: {{baseUrl}}/path
        var url = $"{{{{{BaseUrlVar}}}}}{pathTemplate}";

        // Extract path params from the URL template and resolve example values from
        // any matching in:path parameter definitions.
        var pathParams = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in PathParamRegex().Matches(pathTemplate))
        {
            var paramName = m.Groups[1].Value;
            var paramDef  = mergedParams.FirstOrDefault(p =>
                string.Equals(GetString(p, "in"),   "path",     StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetString(p, "name"),  paramName, StringComparison.OrdinalIgnoreCase));
            var example = paramDef.ValueKind != JsonValueKind.Undefined
                ? GetExampleString(paramDef)
                : null;
            pathParams[paramName] = example ?? string.Empty;
        }

        // Query params
        var queryParams = new List<RequestKv>();
        foreach (var p in mergedParams.Where(IsQueryParam))
        {
            var pname    = GetString(p, "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pname)) continue;
            var required = GetBool(p, "required") ?? false;
            // Try to get an example value.
            var example  = GetExampleString(p);
            queryParams.Add(new RequestKv(pname, example ?? string.Empty, required));
        }

        // Request body (OAS3) or body parameter (Swagger 2)
        var (bodyType, bodyContent) = ExtractBody(op, mergedParams, root);

        // Headers: only in:header parameters explicitly defined in the spec.
        // Content-Type is managed by Callsmith based on the selected body type
        // and must not be added here.
        var headers = new List<RequestKv>();
        foreach (var p in mergedParams.Where(IsHeaderParam))
        {
            var hname = GetString(p, "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(hname)) continue;
            var required = GetBool(p, "required") ?? false;
            var example  = GetExampleString(p);
            headers.Add(new RequestKv(hname, example ?? string.Empty, required));
        }

        return new ImportedRequest
        {
            Name        = name,
            Method      = HttpMethodFromString(methodUpper),
            Url         = url,
            Description = description,
            Headers     = headers,
            QueryParams = queryParams,
            PathParams  = pathParams,
            BodyType    = bodyType,
            Body        = bodyContent,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Parameter helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a list of merged parameter elements. Operation-level parameters
    /// (same name + in) override path-level ones.
    /// </summary>
    private static IReadOnlyList<JsonElement> MergeParameters(
        JsonElement pathLevelParams, JsonElement op)
    {
        var result = new List<JsonElement>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Operation-level params take priority
        if (op.TryGetProperty("parameters", out var opParams)
            && opParams.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in opParams.EnumerateArray())
            {
                result.Add(p);
                seen.Add(ParamKey(p));
            }
        }

        // Path-level params that aren't overridden
        if (pathLevelParams.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pathLevelParams.EnumerateArray())
            {
                if (seen.Add(ParamKey(p)))
                    result.Add(p);
            }
        }

        return result;
    }

    private static string ParamKey(JsonElement p)
    {
        var name = GetString(p, "name") ?? string.Empty;
        var @in  = GetString(p, "in")   ?? string.Empty;
        return $"{@in}:{name}";
    }

    private static bool IsQueryParam(JsonElement p)
        => string.Equals(GetString(p, "in"), "query", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeaderParam(JsonElement p)
        => string.Equals(GetString(p, "in"), "header", StringComparison.OrdinalIgnoreCase);

    private static string? GetExampleString(JsonElement param)
    {
        // Try "example" directly
        if (param.TryGetProperty("example", out var ex) && ex.ValueKind != JsonValueKind.Null)
            return JsonElementToString(ex);

        // Try schema.example
        if (param.TryGetProperty("schema", out var schema))
        {
            if (schema.TryGetProperty("example", out var schEx) && schEx.ValueKind != JsonValueKind.Null)
                return JsonElementToString(schEx);

            if (schema.TryGetProperty("default", out var def) && def.ValueKind != JsonValueKind.Null)
                return JsonElementToString(def);

            // Produce a type-based placeholder (consistent with GenerateStringNode for body examples)
            var type = GetString(schema, "type");
            if (type is not null)
            {
                return type.ToLowerInvariant() switch
                {
                    "integer" or "number" => "0",
                    "boolean"             => "true",
                    "array"               => "[]",
                    "string"              => GetStringFormatPlaceholder(schema),
                    _                     => null,
                };
            }
        }

        return null;
    }

    private static string JsonElementToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        _                    => el.GetRawText(),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Request body
    // ─────────────────────────────────────────────────────────────────────────

    private static (string bodyType, string? body) ExtractBody(
        JsonElement op, IReadOnlyList<JsonElement> mergedParams, JsonElement root)
    {
        // ── OAS3 requestBody ──────────────────────────────────────────────────
        if (op.TryGetProperty("requestBody", out var rb) && rb.ValueKind == JsonValueKind.Object)
        {
            // Resolve $ref if present
            rb = ResolveRef(rb, root) ?? rb;

            if (rb.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Object)
            {
                // Prefer application/json
                if (content.TryGetProperty("application/json", out var jsonContent))
                    return ExtractJsonBody(jsonContent, root);

                // application/x-www-form-urlencoded
                if (content.TryGetProperty("application/x-www-form-urlencoded", out _))
                    return (BodyTypes.Form, null);

                // multipart/form-data
                if (content.TryGetProperty("multipart/form-data", out _))
                    return (BodyTypes.Multipart, null);

                // Any other content type that looks like JSON
                foreach (var ct in content.EnumerateObject())
                {
                    if (ct.Name.Contains("json", StringComparison.OrdinalIgnoreCase))
                        return ExtractJsonBody(ct.Value, root);
                }

                // Fallback: first content type
                foreach (var ct in content.EnumerateObject())
                    return (BodyTypes.Text, null);
            }

            return (BodyTypes.Json, "{}");
        }

        // ── Swagger 2 body parameter ──────────────────────────────────────────
        foreach (var p in mergedParams)
        {
            var @in = GetString(p, "in");
            if (string.Equals(@in, "body", StringComparison.OrdinalIgnoreCase))
            {
                var body = p.TryGetProperty("schema", out var bodySchema)
                    ? GenerateExampleJson(bodySchema, root)
                    : "{}";
                return (BodyTypes.Json, body);
            }

            if (string.Equals(@in, "formData", StringComparison.OrdinalIgnoreCase))
                return (BodyTypes.Form, null);
        }

        return (BodyTypes.None, null);
    }

    private static (string bodyType, string? body) ExtractJsonBody(
        JsonElement mediaType, JsonElement root)
    {
        if (!mediaType.TryGetProperty("schema", out var schema))
            return (BodyTypes.Json, "{}");

        // Prefer an explicit media-type-level example (highest precedence).
        if (mediaType.TryGetProperty("example", out var ex) && ex.ValueKind != JsonValueKind.Null)
            return (BodyTypes.Json, ex.GetRawText());

        // Generate an example by walking the schema tree and resolving all $refs.
        return (BodyTypes.Json, GenerateExampleJson(schema, root));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Example generation from schema
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a JSON example string from the given schema element.
    /// Resolves <c>$ref</c> pointers recursively (with circular-ref protection),
    /// honours explicit <c>example</c> / <c>default</c> fields, handles
    /// <c>allOf</c> / <c>anyOf</c> / <c>oneOf</c>, and falls back to
    /// type-appropriate placeholder values.
    /// Returns <c>"{}"</c> when no meaningful example can be derived.
    /// </summary>
    private static string GenerateExampleJson(JsonElement schema, JsonElement root)
    {
        var node = GenerateExampleNode(schema, root, new HashSet<string>(StringComparer.Ordinal));
        return node?.ToJsonString(IndentedJsonOptions) ?? "{}";
    }

    private static JsonNode? GenerateExampleNode(
        JsonElement schema, JsonElement root, HashSet<string> visitedRefs)
    {
        // Resolve $ref — track visited refs to break circular chains.
        if (schema.TryGetProperty("$ref", out var refEl))
        {
            var refStr = refEl.GetString();
            if (string.IsNullOrEmpty(refStr) || visitedRefs.Contains(refStr))
                return null;

            var resolved = ResolveRef(schema, root);
            if (resolved is null) return null;

            visitedRefs.Add(refStr);
            var result = GenerateExampleNode(resolved.Value, root, visitedRefs);
            visitedRefs.Remove(refStr);
            return result;
        }

        // Explicit example wins over synthesis.
        if (schema.TryGetProperty("example", out var ex) && ex.ValueKind != JsonValueKind.Null)
            return JsonNode.Parse(ex.GetRawText());

        // Explicit default is also preferred over synthesis.
        if (schema.TryGetProperty("default", out var def) && def.ValueKind != JsonValueKind.Null)
            return JsonNode.Parse(def.GetRawText());

        // allOf: merge all sub-schemas into one object.
        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
            return GenerateAllOfNode(allOf, root, visitedRefs);

        // anyOf / oneOf: use the first sub-schema that yields a non-null example.
        foreach (var combiner in (ReadOnlySpan<string>)["anyOf", "oneOf"])
        {
            if (!schema.TryGetProperty(combiner, out var combo)
                || combo.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var sub in combo.EnumerateArray())
            {
                var node = GenerateExampleNode(sub, root, visitedRefs);
                if (node is not null) return node;
            }
        }

        // Type-based synthesis.
        var type = GetString(schema, "type");
        return type?.ToLowerInvariant() switch
        {
            "object"              => GenerateObjectNode(schema, root, visitedRefs),
            "array"               => GenerateArrayNode(schema, root, visitedRefs),
            "string"              => GenerateStringNode(schema),
            "integer" or "number" => JsonValue.Create(0)!,
            "boolean"             => JsonValue.Create(true)!,
            // JSON null type — return null so anyOf/oneOf skips this option.
            "null"                => null,
            // No explicit type — treat as object only when properties are defined so that
            // empty/untyped sub-schemas in anyOf/oneOf don't crowd out more useful options.
            _                     => schema.TryGetProperty("properties", out _)
                                         ? GenerateObjectNode(schema, root, visitedRefs)
                                         : null,
        };
    }

    private static JsonObject GenerateObjectNode(
        JsonElement schema, JsonElement root, HashSet<string> visitedRefs)
    {
        var obj = new JsonObject();
        if (!schema.TryGetProperty("properties", out var props)
            || props.ValueKind != JsonValueKind.Object)
        {
            return obj;
        }

        foreach (var prop in props.EnumerateObject())
            obj[prop.Name] = GenerateExampleNode(prop.Value, root, visitedRefs);

        return obj;
    }

    private static JsonArray GenerateArrayNode(
        JsonElement schema, JsonElement root, HashSet<string> visitedRefs)
    {
        var arr = new JsonArray();
        if (schema.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Object)
        {
            arr.Add(GenerateExampleNode(items, root, visitedRefs));
        }

        return arr;
    }

    /// <summary>
    /// Merges the properties from every sub-schema in an <c>allOf</c> array
    /// into a single <see cref="JsonObject"/>.
    /// </summary>
    private static JsonObject GenerateAllOfNode(
        JsonElement allOf, JsonElement root, HashSet<string> visitedRefs)
    {
        var merged = new JsonObject();
        foreach (var sub in allOf.EnumerateArray())
        {
            if (GenerateExampleNode(sub, root, visitedRefs) is not JsonObject subObj)
                continue;

            foreach (var kvp in subObj.ToList())
                merged[kvp.Key] = kvp.Value?.DeepClone();
        }

        return merged;
    }

    private static JsonValue GenerateStringNode(JsonElement schema)
    {
        // Use the first enum value when available.
        if (schema.TryGetProperty("enum", out var enumEl)
            && enumEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in enumEl.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.String)
                    return JsonValue.Create(v.GetString())!;
            }
        }

        return JsonValue.Create(GetStringFormatPlaceholder(schema))!;
    }

    /// <summary>
    /// Returns a format-appropriate placeholder string for a schema with <c>type: string</c>.
    /// Shared by <see cref="GenerateStringNode"/> (body examples) and
    /// <see cref="GetExampleString"/> (parameter placeholders).
    /// </summary>
    private static string GetStringFormatPlaceholder(JsonElement schema)
    {
        var format = GetString(schema, "format")?.ToLowerInvariant();
        return format switch
        {
            "date"           => "2024-01-01",
            "date-time"      => "2024-01-01T00:00:00Z",
            "uuid" or "guid" => "00000000-0000-0000-0000-000000000000",
            "email"          => "user@example.com",
            "uri" or "url"   => "https://example.com",
            _                => "string",
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // $ref resolution
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a JSON <c>$ref</c> pointer within the same document.
    /// Only supports internal references of the form <c>#/path/to/component</c>.
    /// Returns <c>null</c> when the reference cannot be resolved.
    /// </summary>
    private static JsonElement? ResolveRef(JsonElement el, JsonElement root)
    {
        if (!el.TryGetProperty("$ref", out var refEl))
            return el;

        var refStr = refEl.GetString();
        if (string.IsNullOrEmpty(refStr) || !refStr.StartsWith('#'))
            return null;

        var parts = refStr.TrimStart('#').TrimStart('/').Split('/');
        var current = root;
        foreach (var part in parts)
        {
            if (!current.TryGetProperty(part, out current))
                return null;
        }
        return current;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Misc helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string? GetFirstTag(JsonElement op)
    {
        if (!op.TryGetProperty("tags", out var tagsEl)
            || tagsEl.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var t in tagsEl.EnumerateArray())
        {
            var s = t.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }

    /// <summary>Navigates a chain of property names and returns the string value, or null.</summary>
    private static string? GetString(JsonElement el, params string[] keys)
    {
        var current = el;
        foreach (var key in keys)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(key, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool? GetBool(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var val)) return null;
        if (val.ValueKind == JsonValueKind.True)  return true;
        if (val.ValueKind == JsonValueKind.False) return false;
        // YAML→JSON conversion via YamlDotNet may serialize booleans as strings.
        if (val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString();
            if (string.Equals(s, "true",  StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }

    private static HttpMethod HttpMethodFromString(string method) => method switch
    {
        "GET"     => HttpMethod.Get,
        "POST"    => HttpMethod.Post,
        "PUT"     => HttpMethod.Put,
        "PATCH"   => HttpMethod.Patch,
        "DELETE"  => HttpMethod.Delete,
        "HEAD"    => HttpMethod.Head,
        "OPTIONS" => HttpMethod.Options,
        "TRACE"   => HttpMethod.Trace,
        _         => new HttpMethod(method),
    };
}
