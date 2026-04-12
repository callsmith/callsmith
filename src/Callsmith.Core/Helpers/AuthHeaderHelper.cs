using System.Text;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Core.Services;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Shared helper for applying authentication headers and query-string parameters to a request.
/// Used by both the send path (<c>RequestTabViewModel</c>) and the history reconstruction path
/// (<c>HistorySentViewBuilder</c>) to ensure consistent behaviour.
/// </summary>
public static class AuthHeaderHelper
{
    /// <summary>
    /// Resolves authentication credentials against the supplied variable map and writes the
    /// appropriate <c>Authorization</c> header (or query-string parameter for API-key-in-query
    /// auth) into <paramref name="headers"/>, updating <paramref name="url"/> when needed.
    /// </summary>
    /// <param name="auth">Authentication configuration to apply.</param>
    /// <param name="headers">Header dictionary to mutate.</param>
    /// <param name="requestUrl">The request URL before auth is applied.</param>
    /// <param name="url">
    /// Returns the (possibly modified) URL after auth is applied. For API-key-in-query auth
    /// the key/value are appended to the query string.
    /// </param>
    /// <param name="vars">Variable substitution map.</param>
    /// <param name="mockGenerators">
    /// Optional mock-data generators forwarded to the substitution engine.
    /// Pass <see langword="null"/> when replaying from history.
    /// </param>
    /// <param name="secretVariableNames">
    /// Optional set of variable names whose values are secrets. When provided together with
    /// <paramref name="collector"/>, secret values are recorded as <see cref="VariableBinding"/>s
    /// with <see cref="VariableBinding.IsSecret"/> set to <see langword="true"/>.
    /// </param>
    /// <param name="collector">
    /// Optional list to which variable bindings are appended as variables are resolved.
    /// Pass <see langword="null"/> when collection is not required (e.g. history replay).
    /// </param>
    public static void ApplyAuthHeaders(
        AuthConfig auth,
        Dictionary<string, string> headers,
        string requestUrl,
        out string url,
        IReadOnlyDictionary<string, string> vars,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators = null,
        IReadOnlySet<string>? secretVariableNames = null,
        IList<VariableBinding>? collector = null)
    {
        url = requestUrl;

        string Resolve(string? template) =>
            collector is not null && secretVariableNames is not null
                ? VariableSubstitutionService.SubstituteCollecting(
                    template, vars, secretVariableNames, collector, mockGenerators) ?? template ?? string.Empty
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
                var resolvedName  = Resolve(auth.ApiKeyName);
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
}
