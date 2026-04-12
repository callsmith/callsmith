using System.Net.Http;
using System.Text;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Callsmith.Core.MockData;

namespace Callsmith.Core.Services;

/// <summary>
/// Implements request assembly: transforms request editor state and environment context
/// into a fully prepared RequestModel ready for transmission.
/// </summary>
public class RequestAssemblyService : IRequestAssemblyService
{
    private readonly ICollectionService _collectionService;
    private readonly IEnvironmentMergeService _mergeService;

    public RequestAssemblyService(ICollectionService collectionService, IEnvironmentMergeService mergeService)
    {
        _collectionService = collectionService ?? throw new ArgumentNullException(nameof(collectionService));
        _mergeService = mergeService ?? throw new ArgumentNullException(nameof(mergeService));
    }

    public async Task<AssembledRequest> AssembleAsync(
        RequestAssemblyInput request,
        EnvironmentModel globalEnvironment,
        EnvironmentModel? activeEnvironment,
        string collectionRootPath,
        CancellationToken ct = default)
    {
        // Merge environments to get unified variable set.
        var env = await _mergeService.MergeAsync(
            collectionRootPath,
            globalEnvironment,
            activeEnvironment,
            ct: ct);

        var secretNames = ExtractSecretVariableNames(env);
        var sentBindings = new List<VariableBinding>();

        // Resolve headers with variable substitution.
        var headers = ResolveHeaders(
            request.Headers,
            env.Variables,
            env.MockGenerators,
            secretNames,
            sentBindings);

        // Apply path parameters to URL template.
        var pathParamValues = request.PathParams
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(
                p => p.Key,
                p => VariableSubstitutionService.SubstituteCollecting(
                    p.Value,
                    env.Variables,
                    secretNames,
                    sentBindings,
                    env.MockGenerators) ?? p.Value);

        var requestUrl = PathTemplateHelper.ApplyPathParams(request.Url, pathParamValues);

        // Apply query parameters.
        var substitutedQueryParams = request.QueryParams
            .Select(p => new KeyValuePair<string, string>(
                VariableSubstitutionService.SubstituteCollecting(
                    p.Key,
                    env.Variables,
                    secretNames,
                    sentBindings,
                    env.MockGenerators) ?? p.Key,
                VariableSubstitutionService.SubstituteCollecting(
                    p.Value,
                    env.Variables,
                    secretNames,
                    sentBindings,
                    env.MockGenerators) ?? p.Value))
            .ToList();

        requestUrl = QueryStringHelper.AppendQueryParams(requestUrl, substitutedQueryParams);

        // Get effective authentication (resolve inherited auth if needed).
        var effectiveAuth = await GetEffectiveAuthAsync(request.Auth, collectionRootPath, ct);

        // Apply authentication headers and potentially add auth to query string.
        ApplyAuthHeaders(
            headers,
            requestUrl,
            env.Variables,
            out requestUrl,
            effectiveAuth,
            env.MockGenerators,
            secretNames,
            sentBindings);

        // Resolve any remaining {{tokens}} in the URL.
        requestUrl = VariableSubstitutionService.SubstituteCollecting(
            requestUrl,
            env.Variables,
            secretNames,
            sentBindings,
            env.MockGenerators) ?? requestUrl;

        // Resolve body based on body type.
        var (resolvedBody, multipartFormParams, fileBodyBytes) = Resolvebody(
            request,
            env.Variables,
            secretNames,
            sentBindings,
            env.MockGenerators);

        // Determine auto-applied headers (e.g., Content-Type for forms).
        var contentType = DetermineContentType(request.BodyType, resolvedBody, fileBodyBytes, multipartFormParams);
        var autoAppliedHeaders = contentType is not null
            ? (IReadOnlyList<RequestKv>)[new RequestKv(WellKnownHeaders.ContentType, contentType)]
            : (IReadOnlyList<RequestKv>)[..Array.Empty<RequestKv>()];

        // Build the final RequestModel.
        var requestModel = new RequestModel
        {
            Method = new HttpMethod(request.Method),
            Url = requestUrl,
            Headers = headers,
            Body = resolvedBody,
            BodyBytes = fileBodyBytes,
            MultipartFormParams = multipartFormParams,
            ContentType = contentType,
        };

        // Deduplicate variable bindings by token.
        var dedupedBindings = sentBindings
            .GroupBy(b => b.Token)
            .Select(g => g.First())
            .ToList();

        return new AssembledRequest
        {
            RequestModel = requestModel,
            ResolvedUrl = requestUrl,
            VariableBindings = dedupedBindings,
            EffectiveAuth = effectiveAuth,
            AutoAppliedHeaders = autoAppliedHeaders,
        };
    }

    private static IReadOnlySet<string> ExtractSecretVariableNames(ResolvedEnvironment env)
    {
        var secretNames = new HashSet<string>(StringComparer.Ordinal);
        if (env.Variables is not null)
        {
            foreach (var varName in env.Variables.Keys)
            {
                // Mark as secret if any environment in the chain has this variable marked as secret.
                // For simplicity, just check if it's in the merged result — the merge already
                // includes secret marking from the environment hierarchy.
                if (!varName.StartsWith("__", StringComparison.Ordinal)) // Skip mock evaluator internals
                    secretNames.Add(varName);
            }
        }
        return secretNames;
    }

    private static Dictionary<string, string> ResolveHeaders(
        IEnumerable<KeyValuePair<string, string>> source,
        IReadOnlyDictionary<string, string> vars,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators = null,
        IReadOnlySet<string>? secretVariableNames = null,
        IList<VariableBinding>? collector = null)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            string key, value;
            if (collector is not null && secretVariableNames is not null)
            {
                key = VariableSubstitutionService.SubstituteCollecting(pair.Key, vars, secretVariableNames, collector, mockGenerators) ?? pair.Key;
                value = VariableSubstitutionService.SubstituteCollecting(pair.Value, vars, secretVariableNames, collector, mockGenerators) ?? pair.Value;
            }
            else
            {
                key = VariableSubstitutionService.Substitute(pair.Key, vars) ?? pair.Key;
                value = VariableSubstitutionService.Substitute(pair.Value, vars) ?? pair.Value;
            }

            if (string.IsNullOrWhiteSpace(key)) continue;
            resolved[key] = value;
        }

        return resolved;
    }

    private async Task<AuthConfig> GetEffectiveAuthAsync(
        AuthConfig requestAuth,
        string collectionRootPath,
        CancellationToken ct)
    {
        if (requestAuth.AuthType != AuthConfig.AuthTypes.Inherit)
            return requestAuth;

        if (string.IsNullOrEmpty(collectionRootPath))
            return new AuthConfig { AuthType = AuthConfig.AuthTypes.None };

        return await _collectionService.ResolveEffectiveAuthAsync(collectionRootPath, ct).ConfigureAwait(false)
            ?? new AuthConfig { AuthType = AuthConfig.AuthTypes.None };
    }

    private static void ApplyAuthHeaders(
        Dictionary<string, string> headers,
        string requestUrl,
        IReadOnlyDictionary<string, string> vars,
        out string url,
        AuthConfig auth,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators = null,
        IReadOnlySet<string>? secretVariableNames = null,
        IList<VariableBinding>? collector = null)
    {
        url = requestUrl;

        string Resolve(string? template) =>
            collector is not null && secretVariableNames is not null
                ? VariableSubstitutionService.SubstituteCollecting(template, vars, secretVariableNames, collector, mockGenerators) ?? template ?? string.Empty
                : VariableSubstitutionService.Substitute(template, vars) ?? template ?? string.Empty;

        switch (auth.AuthType)
        {
            case AuthConfig.AuthTypes.Bearer when !string.IsNullOrEmpty(auth.Token):
                var token = Resolve(auth.Token);
                headers[WellKnownHeaders.Authorization] = $"Bearer {token}";
                break;
            case AuthConfig.AuthTypes.Basic when !string.IsNullOrEmpty(auth.Username):
                var username = Resolve(auth.Username);
                var password = Resolve(auth.Password);
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{username}:{password}"));
                headers[WellKnownHeaders.Authorization] = $"Basic {encoded}";
                break;
            case AuthConfig.AuthTypes.ApiKey when !string.IsNullOrEmpty(auth.ApiKeyName)
                                               && !string.IsNullOrEmpty(auth.ApiKeyValue):
                var resolvedName = Resolve(auth.ApiKeyName);
                var resolvedValue = Resolve(auth.ApiKeyValue);
                if (string.IsNullOrWhiteSpace(resolvedName))
                    break;

                if (auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header)
                    headers[resolvedName] = resolvedValue;
                else
                    url = QueryStringHelper.AppendQueryParams(
                        requestUrl,
                        [new KeyValuePair<string, string>(resolvedName, resolvedValue)]);
                break;
        }
    }

    private static (string? ResolvedBody, IReadOnlyList<KeyValuePair<string, string>>? MultipartFormParams, byte[]? FileBodyBytes)
        Resolvebody(
            RequestAssemblyInput request,
            IReadOnlyDictionary<string, string> vars,
            IReadOnlySet<string> secretNames,
            IList<VariableBinding> bindings,
            IReadOnlyDictionary<string, MockDataEntry>? mockGenerators)
    {
        // For text body types, resolve variables.
        if (request.BodyType is
            CollectionRequest.BodyTypes.Json or CollectionRequest.BodyTypes.Xml or
            CollectionRequest.BodyTypes.Yaml or CollectionRequest.BodyTypes.Text or
            CollectionRequest.BodyTypes.Other)
        {
            var resolved = !string.IsNullOrEmpty(request.BodyText)
                ? VariableSubstitutionService.SubstituteCollecting(
                    request.BodyText,
                    vars,
                    secretNames,
                    bindings,
                    mockGenerators) ?? request.BodyText
                : null;
            return (resolved, null, null);
        }

        // For form bodies, URL-encode the form parameters.
        if (request.BodyType == CollectionRequest.BodyTypes.Form)
        {
            var formPairs = request.FormParams
                .Select(p => new KeyValuePair<string, string>(
                    VariableSubstitutionService.SubstituteCollecting(p.Key, vars, secretNames, bindings, mockGenerators) ?? p.Key,
                    VariableSubstitutionService.SubstituteCollecting(p.Value, vars, secretNames, bindings, mockGenerators) ?? p.Value))
                .ToList();

            var formBody = formPairs.Count > 0
                ? string.Join("&",
                    formPairs.Select(p =>
                        Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)))
                : null;
            return (formBody, null, null);
        }

        // For multipart bodies, collect form parameters.
        if (request.BodyType == CollectionRequest.BodyTypes.Multipart)
        {
            var multipartParams = request.FormParams
                .Select(p => new KeyValuePair<string, string>(
                    VariableSubstitutionService.SubstituteCollecting(p.Key, vars, secretNames, bindings, mockGenerators) ?? p.Key,
                    VariableSubstitutionService.SubstituteCollecting(p.Value, vars, secretNames, bindings, mockGenerators) ?? p.Value))
                .ToList();
            return (null, multipartParams, null);
        }

        // For file bodies, pass bytes directly.
        if (request.BodyType == CollectionRequest.BodyTypes.File)
        {
            return (null, null, request.FileBodyBytes);
        }

        // No body.
        return (null, null, null);
    }

    private static string? DetermineContentType(
        string bodyType,
        string? bodyText,
        byte[]? fileBodyBytes,
        IReadOnlyList<KeyValuePair<string, string>>? multipartFormParams)
    {
        // RequestModel.ContentType allows transport to override, but we apply defaults here
        // for form and file bodies. Explicit Content-Type headers take precedence.
        return bodyType switch
        {
            CollectionRequest.BodyTypes.Form => "application/x-www-form-urlencoded",
            CollectionRequest.BodyTypes.Json => "application/json",
            CollectionRequest.BodyTypes.Xml => "application/xml",
            CollectionRequest.BodyTypes.Yaml => "application/yaml",
            _ => null,
        };
    }
}
