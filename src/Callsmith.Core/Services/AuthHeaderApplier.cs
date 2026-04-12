using System.Text;
using Callsmith.Core.Helpers;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

/// <summary>
/// Canonical auth-header/query application used by send and history-resolved flows.
/// </summary>
public static class AuthHeaderApplier
{
    public static string Apply(
        AuthConfig auth,
        Dictionary<string, string> headers,
        IReadOnlyDictionary<string, string> vars,
        string url)
    {
        string Resolve(string? template) =>
            VariableSubstitutionService.Substitute(template, vars) ?? template ?? string.Empty;

        return ApplyInternal(auth, headers, url, Resolve);
    }

    public static string ApplyCollecting(
        AuthConfig auth,
        Dictionary<string, string> headers,
        IReadOnlyDictionary<string, string> vars,
        string url,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators,
        IReadOnlySet<string> secretVariableNames,
        IList<VariableBinding> collector)
    {
        string Resolve(string? template) =>
            VariableSubstitutionService.SubstituteCollecting(
                template,
                vars,
                secretVariableNames,
                collector,
                mockGenerators) ?? template ?? string.Empty;

        return ApplyInternal(auth, headers, url, Resolve);
    }

    private static string ApplyInternal(
        AuthConfig auth,
        Dictionary<string, string> headers,
        string url,
        Func<string?, string> resolve)
    {
        switch (auth.AuthType)
        {
            case AuthConfig.AuthTypes.Bearer when !string.IsNullOrEmpty(auth.Token):
                headers[WellKnownHeaders.Authorization] = $"Bearer {resolve(auth.Token)}";
                break;

            case AuthConfig.AuthTypes.Basic when !string.IsNullOrEmpty(auth.Username):
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{resolve(auth.Username)}:{resolve(auth.Password)}"));
                headers[WellKnownHeaders.Authorization] = $"Basic {encoded}";
                break;

            case AuthConfig.AuthTypes.ApiKey
                when !string.IsNullOrEmpty(auth.ApiKeyName)
                  && !string.IsNullOrEmpty(auth.ApiKeyValue):
                var resolvedName = resolve(auth.ApiKeyName);
                var resolvedValue = resolve(auth.ApiKeyValue);
                if (string.IsNullOrWhiteSpace(resolvedName))
                    break;

                if (auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header)
                    headers[resolvedName] = resolvedValue;
                else
                    url = QueryStringHelper.AppendQueryParams(
                        url,
                        [new KeyValuePair<string, string>(resolvedName, resolvedValue)]);
                break;
        }

        return url;
    }
}
