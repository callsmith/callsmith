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
/// Evaluates dynamic environment variable segments by executing referenced collection
/// requests and extracting values from their responses. Results are cached on disk
/// according to each segment's <see cref="DynamicFrequency"/> setting.
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
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        string collectionFolderPath,
        string environmentFilePath,
        IReadOnlyList<EnvironmentVariable> variables,
        IReadOnlyDictionary<string, string> resolvedStaticVariables,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(environmentFilePath);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(resolvedStaticVariables);

        var dynamicVars = variables.Where(v => v.Segments is { Count: > 0 }).ToList();
        if (dynamicVars.Count == 0)
            return resolvedStaticVariables;

        var result = new Dictionary<string, string>(resolvedStaticVariables);
        var cache = await LoadCacheAsync(collectionFolderPath, ct).ConfigureAwait(false);
        var cacheModified = false;

        // Load the collection folder tree once (shared across all dynamic variables).
        CollectionFolder? folder = null;
        try
        {
            folder = await _collectionService.OpenFolderAsync(collectionFolderPath, ct)
                         .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open collection folder for dynamic variable evaluation");
        }

        // Evaluate in two passes so that dynamic variables which reference other dynamic
        // variables in their linked requests work correctly regardless of declaration order.
        // The second pass uses the fully-populated result dict from the first, so any
        // {{varName}} tokens in request headers/auth/body resolve to their evaluated values.
        // Never/IfExpired variables hit the cache on the second pass (no extra HTTP calls);
        // Always-frequency variables make a second call, which is expected for that policy.
        var passes = dynamicVars.Count > 1 ? 2 : 1;
        for (var pass = 0; pass < passes; pass++)
        {
            foreach (var variable in dynamicVars)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var value = await BuildSegmentValueAsync(
                        variable, environmentFilePath, folder, result, cache, ct)
                        .ConfigureAwait(false);

                    if (value != null)
                    {
                        result[variable.Name] = value;
                        cacheModified = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to resolve dynamic variable '{Name}'", variable.Name);
                }
            }
        }

        if (cacheModified)
            await SaveCacheAsync(collectionFolderPath, cache, ct).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc/>
    public async Task<string?> PreviewAsync(
        string collectionFolderPath,
        DynamicValueSegment segment,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(variables);

        CollectionFolder? folder = null;
        try
        {
            folder = await _collectionService.OpenFolderAsync(collectionFolderPath, ct)
                         .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open collection folder for dynamic variable preview");
            return null;
        }

        return await ExecuteAndExtractAsync(segment, folder, variables, ct).ConfigureAwait(false);
    }

    // ─── Request execution ───────────────────────────────────────────────────

    private async Task<string?> BuildSegmentValueAsync(
        EnvironmentVariable variable,
        string environmentFilePath,
        CollectionFolder? folder,
        IReadOnlyDictionary<string, string> vars,
        DynCache cache,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var seg in variable.Segments!)
        {
            if (seg is StaticValueSegment s)
            {
                sb.Append(s.Text);
            }
            else if (seg is MockDataSegment m)
            {
                sb.Append(MockDataCatalog.Generate(m.Category, m.Field));
            }
            else if (seg is DynamicValueSegment d)
            {
                var val = await EvaluateDynamicSegmentAsync(
                    d, environmentFilePath, folder, vars, cache, ct).ConfigureAwait(false);
                sb.Append(val ?? string.Empty);
            }
        }
        return sb.ToString();
    }

    private async Task<string?> EvaluateDynamicSegmentAsync(
        DynamicValueSegment segment,
        string environmentFilePath,
        CollectionFolder? folder,
        IReadOnlyDictionary<string, string> vars,
        DynCache cache,
        CancellationToken ct)
    {
        var cacheKey = MakeCacheKey(environmentFilePath, segment.RequestName, segment.Path);

        var shouldExecute = segment.Frequency switch
        {
            DynamicFrequency.Always => true,
            DynamicFrequency.Never => !cache.ContainsKey(cacheKey),
            DynamicFrequency.IfExpired => ShouldRefresh(cache, cacheKey, segment.ExpiresAfterSeconds ?? 900),
            _ => true,
        };

        if (!shouldExecute && cache.TryGetValue(cacheKey, out var cached))
            return cached.Value;

        if (folder is null)
        {
            _logger.LogWarning(
                "Cannot evaluate dynamic segment for '{RequestName}': collection folder not loaded",
                segment.RequestName);
            return cache.TryGetValue(cacheKey, out var fallback) ? fallback.Value : null;
        }

        var resolved = await ExecuteAndExtractAsync(segment, folder, vars, ct).ConfigureAwait(false);

        // Always cache the result so that subsequent uses (with Never/IfExpired) can use it.
        cache[cacheKey] = new CacheEntry(resolved ?? string.Empty, DateTime.UtcNow);
        return resolved;
    }

    private async Task<string?> ExecuteAndExtractAsync(
        DynamicValueSegment segment,
        CollectionFolder folder,
        IReadOnlyDictionary<string, string> vars,
        CancellationToken ct)
    {
        var stub = FindRequestByName(folder, segment.RequestName);
        if (stub is null)
        {
            _logger.LogWarning(
                "Dynamic variable: request '{RequestName}' not found in collection",
                segment.RequestName);
            return null;
        }

        // Use LoadRequestAsync so that Basic auth passwords retrieved from secret storage
        // (never written to the .callsmith file) are included. The folder tree only holds
        // lightweight stubs that lack the secret-stored password.
        CollectionRequest request;
        try
        {
            request = await _collectionService.LoadRequestAsync(stub.FilePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dynamic variable: could not fully load request '{RequestName}'", segment.RequestName);
            request = stub;
        }

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

        var extracted = JsonPathHelper.Extract(response.Body, segment.Path);
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
        requestUrl = QueryStringHelper.ApplyQueryParams(requestUrl, queryPairs);

        // Headers + auth
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in req.Headers.Where(h => h.IsEnabled))
            headers[h.Key] = VariableSubstitutionService.Substitute(h.Value, vars) ?? h.Value;

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
                var keyValue = VariableSubstitutionService.Substitute(auth.ApiKeyValue ?? string.Empty, vars) ?? string.Empty;
                if (auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header)
                    headers[auth.ApiKeyName] = keyValue;
                else
                    url = QueryStringHelper.ApplyQueryParams(url,
                        [new KeyValuePair<string, string>(auth.ApiKeyName, keyValue)]);
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

    // ─── Request lookup ──────────────────────────────────────────────────────

    /// <summary>
    /// Searches the collection tree for a request whose display name matches
    /// <paramref name="requestName"/>. Supports slash-separated paths:
    /// <c>"FolderName/RequestName"</c>.
    /// </summary>
    private static CollectionRequest? FindRequestByName(CollectionFolder root, string requestName)
    {
        var parts = requestName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? FindRequestInTree(root, parts[0])
            : FindRequestByPath(root, parts);
    }

    private static CollectionRequest? FindRequestByPath(CollectionFolder folder, string[] parts)
    {
        if (parts.Length == 0) return null;
        if (parts.Length == 1)
            return folder.Requests.FirstOrDefault(r =>
                string.Equals(r.Name, parts[0], StringComparison.OrdinalIgnoreCase));

        // Navigate into sub-folder
        var sub = folder.SubFolders.FirstOrDefault(f =>
            string.Equals(f.Name, parts[0], StringComparison.OrdinalIgnoreCase));
        return sub is null ? null : FindRequestByPath(sub, parts[1..]);
    }

    private static CollectionRequest? FindRequestInTree(CollectionFolder folder, string name)
    {
        var direct = folder.Requests.FirstOrDefault(r =>
            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct;

        foreach (var sub in folder.SubFolders)
        {
            var found = FindRequestInTree(sub, name);
            if (found is not null) return found;
        }
        return null;
    }

    // ─── Cache helpers ───────────────────────────────────────────────────────

    private static bool ShouldRefresh(DynCache cache, string key, int lifetimeSeconds)
    {
        if (!cache.TryGetValue(key, out var entry)) return true;
        return (DateTime.UtcNow - entry.CachedAt).TotalSeconds >= lifetimeSeconds;
    }

    private static string MakeCacheKey(string envFilePath, string requestName, string path) =>
        $"{envFilePath}|{requestName}|{path}";

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
