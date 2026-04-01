using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// Evaluates dynamic environment variables (response-body and mock-data types) by
/// executing referenced collection requests and extracting values from responses.
/// Response-body results are cached on disk according to each variable's
/// <see cref="DynamicFrequency"/> setting.
/// <para>
/// Cache location: <c>%LOCALAPPDATA%\Callsmith\dyncache\&lt;collection-hash&gt;.json</c>.
/// </para>
/// </summary>
public sealed class DynamicVariableEvaluatorService : IDynamicVariableEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICollectionService _collectionService;
    private readonly ITransportRegistry _transportRegistry;
    private readonly string _cacheDirectory;
    private readonly ILogger<DynamicVariableEvaluatorService> _logger;

    /// <summary>
    /// Creates an instance using the default OS local-application-data cache location.
    /// </summary>
    public DynamicVariableEvaluatorService(
        ICollectionService collectionService,
        ITransportRegistry transportRegistry,
        ILogger<DynamicVariableEvaluatorService> logger)
        : this(collectionService, transportRegistry, GetDefaultCacheDirectory(), logger)
    {
    }

    /// <summary>Internal constructor that accepts a custom cache path (for testing).</summary>
    internal DynamicVariableEvaluatorService(
        ICollectionService collectionService,
        ITransportRegistry transportRegistry,
        string cacheDirectory,
        ILogger<DynamicVariableEvaluatorService> logger)
    {
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(transportRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        _collectionService = collectionService;
        _transportRegistry = transportRegistry;
        _cacheDirectory = cacheDirectory;
        _logger = logger;
        Directory.CreateDirectory(cacheDirectory);
    }

    /// <inheritdoc/>
    public async Task<ResolvedEnvironment> ResolveAsync(
        string collectionFolderPath,
        string environmentCacheNamespace,
        IReadOnlyList<EnvironmentVariable> variables,
        IReadOnlyDictionary<string, string> staticVariables,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(environmentCacheNamespace);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(staticVariables);

        var resolvedVars = new Dictionary<string, string>(staticVariables);
        var mockGenerators = new Dictionary<string, MockDataEntry>();

        // Separate variables by type.
        var responseBodyVars = new List<EnvironmentVariable>();
        foreach (var v in variables)
        {
            switch (v.VariableType)
            {
                case EnvironmentVariable.VariableTypes.MockData:
                    var entry = v.GetMockEntry();
                    if (entry is not null)
                        mockGenerators[v.Name] = entry;
                    break;

                case EnvironmentVariable.VariableTypes.ResponseBody:
                    if (!string.IsNullOrEmpty(v.ResponseRequestName))
                        responseBodyVars.Add(v);
                    break;

                // Legacy: old segment-based dynamic vars — migrate and handle inline.
                case EnvironmentVariable.VariableTypes.Dynamic:
                    MigrateLegacyVar(v, responseBodyVars, mockGenerators);
                    break;
            }
        }

        if (responseBodyVars.Count == 0)
            return new ResolvedEnvironment { Variables = resolvedVars, MockGenerators = mockGenerators };

        var cache = await LoadCacheAsync(collectionFolderPath, ct).ConfigureAwait(false);
        var cacheModified = false;

        // Two passes so that response-body vars that reference other response-body vars
        // resolve in the right order regardless of declaration order.
        var passes = responseBodyVars.Count > 1 ? 2 : 1;
        for (var pass = 0; pass < passes; pass++)
        {
            foreach (var variable in responseBodyVars)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var value = await EvaluateResponseBodyVarAsync(
                        variable, environmentCacheNamespace, collectionFolderPath, resolvedVars, cache, ct)
                        .ConfigureAwait(false);

                    if (value != null)
                    {
                        resolvedVars[variable.Name] = value;
                        cacheModified = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to resolve response-body variable '{Name}'", variable.Name);
                }
            }
        }

        if (cacheModified)
            await SaveCacheAsync(collectionFolderPath, cache, ct).ConfigureAwait(false);

        return new ResolvedEnvironment { Variables = resolvedVars, MockGenerators = mockGenerators };
    }

    /// <inheritdoc/>
    public async Task<string?> PreviewResponseBodyAsync(
        string collectionFolderPath,
        EnvironmentVariable variable,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(variables);

        if (variable.VariableType != EnvironmentVariable.VariableTypes.ResponseBody
            || string.IsNullOrEmpty(variable.ResponseRequestName)
            || string.IsNullOrEmpty(variable.ResponsePath))
            return null;

        var segment = new DynamicValueSegment
        {
            RequestName = variable.ResponseRequestName,
            Path = variable.ResponsePath,
            Matcher = variable.ResponseMatcher,
            Frequency = variable.ResponseFrequency,
            ExpiresAfterSeconds = variable.ResponseExpiresAfterSeconds,
        };

        string? filePath;
        try
        {
            filePath = await _collectionService
                .ResolveRequestFilePathAsync(collectionFolderPath, segment.RequestName, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not find request for dynamic variable preview");
            return null;
        }

        if (filePath is null)
        {
            _logger.LogWarning(
                "Dynamic variable preview: request '{RequestName}' not found in collection",
                segment.RequestName);
            return null;
        }

        CollectionRequest request;
        try
        {
            request = await _collectionService.LoadRequestAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dynamic variable preview: could not load request '{RequestName}'", segment.RequestName);
            return null;
        }

        return await ExecuteAndExtractAsync(segment, request, variables, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateCacheFromResponseAsync(
        string collectionFolderPath,
        string environmentCacheNamespace,
        Guid requestId,
        string requestName,
        string responseBody,
        IReadOnlyList<EnvironmentVariable> variables,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(environmentCacheNamespace);
        ArgumentNullException.ThrowIfNull(requestName);
        ArgumentNullException.ThrowIfNull(responseBody);
        ArgumentNullException.ThrowIfNull(variables);

        if (string.IsNullOrEmpty(responseBody)) return;

        // Find response-body variables whose linked request matches the one just executed.
        // A variable with a slash-qualified name (e.g. "Auth/core login") is matched by its
        // last segment; a plain name (e.g. "core login") is matched directly.
        var matchingVars = variables
            .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody
                     && !string.IsNullOrEmpty(v.ResponseRequestName)
                     && !string.IsNullOrEmpty(v.ResponsePath)
                     && ResponseRequestNameMatches(v.ResponseRequestName, requestName))
            .ToList();

        if (matchingVars.Count == 0) return;

        var cache = await LoadCacheAsync(collectionFolderPath, ct).ConfigureAwait(false);
        var cacheModified = false;

        foreach (var variable in matchingVars)
        {
            var extracted = ResponseBodyValueExtractor.Extract(
                responseBody, variable.ResponseMatcher, variable.ResponsePath!);
            if (extracted is null)
            {
                _logger.LogDebug(
                    "Cache update skipped for '{Name}': no value at {Matcher} expression '{Path}'",
                    variable.Name, variable.ResponseMatcher, variable.ResponsePath);
                continue;
            }

            var cacheKey = MakeCacheKey(environmentCacheNamespace, requestId, variable.ResponsePath!);
            cache[cacheKey] = new CacheEntry(extracted, DateTime.UtcNow);
            cacheModified = true;
            _logger.LogDebug(
                "Cache updated for '{Name}' (request '{RequestName}', path '{Path}') → '{Value}'",
                variable.Name, variable.ResponseRequestName, variable.ResponsePath, extracted);
        }

        if (cacheModified)
            await SaveCacheAsync(collectionFolderPath, cache, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true when <paramref name="responseRequestName"/> refers to
    /// <paramref name="executedRequestName"/>. A slash-qualified reference (e.g. "Auth/core login")
    /// is matched by its final segment; a plain name is compared directly.
    /// </summary>
    private static bool ResponseRequestNameMatches(string responseRequestName, string executedRequestName)
    {
        var lastSlash = responseRequestName.LastIndexOf('/');
        var leaf = lastSlash >= 0
            ? responseRequestName[(lastSlash + 1)..]
            : responseRequestName;
        return string.Equals(leaf, executedRequestName, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Request execution ───────────────────────────────────────────────────

    private async Task<string?> EvaluateResponseBodyVarAsync(
        EnvironmentVariable variable,
        string environmentCacheNamespace,
        string collectionFolderPath,
        IReadOnlyDictionary<string, string> vars,
        DynCache cache,
        CancellationToken ct)
    {
        var segment = new DynamicValueSegment
        {
            RequestName = variable.ResponseRequestName!,
            Path = variable.ResponsePath ?? string.Empty,
            Matcher = variable.ResponseMatcher,
            Frequency = variable.ResponseFrequency,
            ExpiresAfterSeconds = variable.ResponseExpiresAfterSeconds,
        };

        // Resolve the file path first (cheap — directory enumeration / filename match).
        string? filePath;
        try
        {
            filePath = await _collectionService
                .ResolveRequestFilePathAsync(collectionFolderPath, segment.RequestName, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dynamic variable: failed to locate request '{RequestName}'", segment.RequestName);
            return null;
        }

        if (filePath is null)
        {
            _logger.LogWarning(
                "Dynamic variable: request '{RequestName}' not found in collection",
                variable.Name);
            return null;
        }

        // Load the request to obtain its stable RequestId for the cache key.
        // This single file read lets us use a rename-stable key (the persisted GUID for
        // .callsmith files) and also means we won't need to load the file a second time
        // when we go on to execute the request.
        CollectionRequest request;
        try
        {
            request = await _collectionService.LoadRequestAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dynamic variable: could not load request '{RequestName}'", segment.RequestName);
            return null;
        }

        // Use the persisted RequestId when available so the cache key survives renames.
        // Fall back to a deterministic GUID from the request name (e.g. Bruno collections
        // where the identity is derived from the display path, not a stored GUID).
        var requestCacheId = request.RequestId ?? DeterministicRequestGuid(segment.RequestName);
        var cacheKey = MakeCacheKey(environmentCacheNamespace, requestCacheId, segment.Path);

        var shouldExecute = segment.Frequency switch
        {
            DynamicFrequency.Always => true,
            DynamicFrequency.Never => !cache.ContainsKey(cacheKey),
            DynamicFrequency.IfExpired => ShouldRefresh(cache, cacheKey, segment.ExpiresAfterSeconds ?? 900),
            _ => true,
        };

        if (!shouldExecute && cache.TryGetValue(cacheKey, out var cached))
            return cached.Value;

        // Pass the already-loaded request directly — no second file read needed.
        var resolved = await ExecuteAndExtractAsync(segment, request, vars, ct).ConfigureAwait(false);
        if (resolved is not null)
            cache[cacheKey] = new CacheEntry(resolved, DateTime.UtcNow);
        return resolved;
    }

    /// <summary>
    /// Migrates a legacy segment-based <see cref="EnvironmentVariable.VariableTypes.Dynamic"/> variable
    /// to the new typed model by inspecting its <see cref="EnvironmentVariable.Segments"/>.
    /// A single <see cref="MockDataSegment"/> becomes a mock generator entry.
    /// A single <see cref="DynamicValueSegment"/> is added to the response-body list.
    /// Composites and unrecognised forms are silently ignored.
    /// </summary>
    private static void MigrateLegacyVar(
        EnvironmentVariable variable,
        List<EnvironmentVariable> responseBodyVars,
        Dictionary<string, MockDataEntry> mockGenerators)
    {
        if (variable.Segments is not { Count: > 0 }) return;

        // Pure single-segment → migrate cleanly
        if (variable.Segments.Count == 1)
        {
            switch (variable.Segments[0])
            {
                case MockDataSegment m:
                    var entry = MockDataCatalog.All.FirstOrDefault(
                        e => e.Category == m.Category && e.Field == m.Field);
                    if (entry is not null)
                        mockGenerators[variable.Name] = entry;
                    return;

                case DynamicValueSegment d:
                    responseBodyVars.Add(new EnvironmentVariable
                    {
                        Name = variable.Name,
                        Value = variable.Value,
                        IsSecret = variable.IsSecret,
                        VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                        ResponseRequestName = d.RequestName,
                        ResponsePath = d.Path,
                        ResponseFrequency = d.Frequency,
                        ResponseExpiresAfterSeconds = d.ExpiresAfterSeconds,
                    });
                    return;
            }
        }

        // Composite: handle each segment independently (mock → generator; dynamic → response-body).
        foreach (var seg in variable.Segments)
        {
            if (seg is MockDataSegment ms)
            {
                var e = MockDataCatalog.All.FirstOrDefault(
                    x => x.Category == ms.Category && x.Field == ms.Field);
                if (e is not null)
                    mockGenerators.TryAdd(variable.Name, e);
            }
            else if (seg is DynamicValueSegment ds)
            {
                responseBodyVars.Add(new EnvironmentVariable
                {
                    Name = variable.Name,
                    Value = variable.Value,
                    IsSecret = variable.IsSecret,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                    ResponseRequestName = ds.RequestName,
                    ResponsePath = ds.Path,
                    ResponseFrequency = ds.Frequency,
                    ResponseExpiresAfterSeconds = ds.ExpiresAfterSeconds,
                });
                break; // Only migrate the first dynamic segment from a composite.
            }
        }
    }

    private async Task<string?> ExecuteAndExtractAsync(
        DynamicValueSegment segment,
        CollectionRequest request,
        IReadOnlyDictionary<string, string> vars,
        CancellationToken ct)
    {
        var requestModel = BuildRequestModel(request, vars);

        ResponseModel response;
        try
        {
            var transport = _transportRegistry.Resolve(requestModel);
            response = await transport.SendAsync(requestModel, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dynamic variable: request '{RequestName}' failed to execute", segment.RequestName);
            return null;
        }

        if (string.IsNullOrEmpty(response.Body)) return null;

        var extracted = ResponseBodyValueExtractor.Extract(
            response.Body, segment.Matcher, segment.Path);
        _logger.LogDebug(
            "Dynamic variable '{RequestName}' + path '{Path}' → '{Value}'",
            segment.RequestName, segment.Path, extracted);

        return extracted;
    }

    // ─── Request building ────────────────────────────────────────────────────

    private static RequestModel BuildRequestModel(
        CollectionRequest req, IReadOnlyDictionary<string, string> vars)
    {
        // Substitute URL, path params
        var pathParamValues = req.PathParams.ToDictionary(
            kv => kv.Key,
            kv => VariableSubstitutionService.Substitute(kv.Value, vars) ?? kv.Value);

        var baseUrl = QueryStringHelper.GetBaseUrl(req.Url);
        var requestUrl = PathTemplateHelper.ApplyPathParams(baseUrl, pathParamValues);

        // Query params
        var queryPairs = req.QueryParams
            .Where(p => p.IsEnabled)
            .Select(p => new KeyValuePair<string, string>(
                VariableSubstitutionService.Substitute(p.Key, vars) ?? p.Key,
                VariableSubstitutionService.Substitute(p.Value, vars) ?? p.Value))
            .ToList();
        requestUrl = QueryStringHelper.AppendQueryParams(requestUrl, queryPairs);

        // Headers + auth
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in req.Headers.Where(h => h.IsEnabled))
        {
            var key = VariableSubstitutionService.Substitute(h.Key, vars) ?? h.Key;
            if (string.IsNullOrWhiteSpace(key))
                continue;

            headers[key] = VariableSubstitutionService.Substitute(h.Value, vars) ?? h.Value;
        }

        ApplyAuth(req.Auth, headers, vars, ref requestUrl);

        // Final variable substitution on URL
        requestUrl = VariableSubstitutionService.Substitute(requestUrl, vars) ?? requestUrl;

        // Body
        string? body = null;
        string? contentType = null;
        if (req.BodyType != CollectionRequest.BodyTypes.None)
        {
            (body, contentType) = BuildBody(req, vars);
        }

        return new RequestModel
        {
            Method = req.Method,
            Url = requestUrl,
            Headers = headers,
            Body = body,
            ContentType = contentType,
        };
    }

    private static void ApplyAuth(
        AuthConfig auth,
        Dictionary<string, string> headers,
        IReadOnlyDictionary<string, string> vars,
        ref string url)
    {
        switch (auth.AuthType)
        {
            case AuthConfig.AuthTypes.Bearer when !string.IsNullOrEmpty(auth.Token):
                var token = VariableSubstitutionService.Substitute(auth.Token, vars) ?? auth.Token;
                headers["Authorization"] = $"Bearer {token}";
                break;

            case AuthConfig.AuthTypes.Basic when !string.IsNullOrEmpty(auth.Username):
                var user = VariableSubstitutionService.Substitute(auth.Username, vars) ?? auth.Username;
                var pass = VariableSubstitutionService.Substitute(auth.Password ?? string.Empty, vars) ?? string.Empty;
                headers["Authorization"] = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"))}";
                break;

            case AuthConfig.AuthTypes.ApiKey when !string.IsNullOrEmpty(auth.ApiKeyName):
                var keyName = VariableSubstitutionService.Substitute(auth.ApiKeyName, vars) ?? auth.ApiKeyName;
                var keyValue = VariableSubstitutionService.Substitute(auth.ApiKeyValue ?? string.Empty, vars) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(keyName))
                    break;

                if (auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header)
                    headers[keyName] = keyValue;
                else
                    url = QueryStringHelper.AppendQueryParams(url,
                        [new KeyValuePair<string, string>(keyName, keyValue)]);
                break;
        }
    }

    private static (string? body, string? contentType) BuildBody(
        CollectionRequest req, IReadOnlyDictionary<string, string> vars)
    {
        var contentType = req.BodyType switch
        {
            CollectionRequest.BodyTypes.Json => "application/json",
            CollectionRequest.BodyTypes.Text => "text/plain",
            CollectionRequest.BodyTypes.Xml => "application/xml",
            CollectionRequest.BodyTypes.Form => "application/x-www-form-urlencoded",
            _ => null,
        };

        if (req.BodyType == CollectionRequest.BodyTypes.Form)
        {
            var formPairs = req.FormParams
                .Select(p => new KeyValuePair<string, string>(
                    VariableSubstitutionService.Substitute(p.Key, vars) ?? p.Key,
                    VariableSubstitutionService.Substitute(p.Value, vars) ?? p.Value))
                .ToList();
            var body = string.Join("&",
                formPairs.Select(p =>
                    Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            return (body, contentType);
        }

        if (!string.IsNullOrEmpty(req.Body))
        {
            var substituted = VariableSubstitutionService.Substitute(req.Body, vars) ?? req.Body;
            return (substituted, contentType);
        }

        return (null, contentType);
    }

    // ─── Cache helpers ───────────────────────────────────────────────────────

    private static bool ShouldRefresh(DynCache cache, string key, int lifetimeSeconds)
    {
        if (!cache.TryGetValue(key, out var entry)) return true;
        return (DateTime.UtcNow - entry.CachedAt).TotalSeconds >= lifetimeSeconds;
    }

    private static string MakeCacheKey(string environmentCacheNamespace, Guid requestId, string path) =>
        $"{environmentCacheNamespace}|{requestId:N}|{path}";

    /// <summary>
    /// Produces a deterministic <see cref="Guid"/> from a request name for use as the
    /// cache key when a request has not yet been assigned a stable <see cref="CollectionRequest.RequestId"/>.
    /// Uses the exact casing of the name so that case-distinct names produce distinct GUIDs.
    /// </summary>
    private static Guid DeterministicRequestGuid(string requestName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(requestName));
        return new Guid(hash[..16]);
    }

    private async Task<DynCache> LoadCacheAsync(string collectionFolderPath, CancellationToken ct)
    {
        var path = GetCacheFilePath(collectionFolderPath);
        if (!File.Exists(path)) return new DynCache();

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DynCache>(stream, JsonOptions, ct)
                       .ConfigureAwait(false) ?? new DynCache();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogDebug(ex, "Could not read dynamic variable cache at '{Path}'", path);
            return new DynCache();
        }
    }

    private async Task SaveCacheAsync(
        string collectionFolderPath, DynCache cache, CancellationToken ct)
    {
        var path = GetCacheFilePath(collectionFolderPath);
        try
        {
            await using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
            await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save dynamic variable cache to '{Path}'", path);
        }
    }

    private string GetCacheFilePath(string collectionFolderPath)
    {
        var normalised = Path.GetFullPath(collectionFolderPath)
                             .ToLowerInvariant()
                             .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Path.Combine(_cacheDirectory, Convert.ToHexString(hash) + ".json");
    }

    private static string GetDefaultCacheDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Callsmith", "dyncache");
    }

    // ─── Cache DTOs ──────────────────────────────────────────────────────────

    // Alias for readability
    private sealed class DynCache : Dictionary<string, CacheEntry> { }

    private sealed class CacheEntry
    {
        public string Value { get; init; } = string.Empty;
        public DateTime CachedAt { get; init; }

        // Required for JSON deserialization
        public CacheEntry() { }

        public CacheEntry(string value, DateTime cachedAt)
        {
            Value = value;
            CachedAt = cachedAt;
        }
    }
}
