using System.Net.Http;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

/// <summary>
/// Reconstructs the wire-level ("Resolved") representation of a request from a
/// <see cref="ConfiguredRequestSnapshot"/> and its associated <see cref="VariableBinding"/> list,
/// without requiring a live environment or collection on disk.
/// <para>
/// Use cases:
/// <list type="bullet">
///   <item>Rendering the "Resolved" detail tab in the history UI.</item>
///   <item>Powering the "Replay exact" re-send action (produces a ready-to-dispatch
///     <see cref="RequestModel"/>).</item>
/// </list>
/// </para>
/// </summary>
public static class HistorySentViewBuilder
{
    /// <summary>
    /// Builds a <see cref="RequestModel"/> from a <see cref="ConfiguredRequestSnapshot"/>
    /// and its corresponding variable bindings, exactly as it was (or would have been) sent.
    /// </summary>
    /// <param name="snapshot">The configured snapshot captured at send time.</param>
    /// <param name="bindings">
    /// All variable substitutions that occurred during the original send. Secret bindings
    /// should already be decrypted (call
    /// <see cref="Abstractions.IHistoryService.RevealSensitiveFieldsAsync"/> first if needed).
    /// </param>
    public static RequestModel Build(
        ConfiguredRequestSnapshot snapshot,
        IReadOnlyList<VariableBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(bindings);

        // Build a flat variable map from the bindings for reuse across all field resolutions.
        // Each binding's Token is e.g. "{{baseUrl}}" — strip the braces for the lookup key.
        var vars = BuildVariableMap(bindings);

        // 1. Resolve path params and substitute into the base URL.
        var resolvedPathParams = snapshot.PathParams
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(
                p => p.Key,
                   p => Substitute(p.Value, vars) ?? p.Value);

        var requestUrl = PathTemplateHelper.ApplyPathParams(snapshot.Url, resolvedPathParams);

        // 2. Resolve and append query params.
        var resolvedQueryParams = snapshot.QueryParams
            .Where(p => p.IsEnabled)
            .Select(p => new KeyValuePair<string, string>(
                Substitute(p.Key, vars) ?? p.Key,
                Substitute(p.Value, vars) ?? p.Value))
            .ToList();

        requestUrl = QueryStringHelper.AppendQueryParams(requestUrl, resolvedQueryParams);

        // 3. Resolve headers (user-authored).
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in snapshot.Headers.Where(h => h.IsEnabled))
        {
            var key = Substitute(h.Key, vars) ?? h.Key;
            if (!string.IsNullOrWhiteSpace(key))
                headers[key] = Substitute(h.Value, vars) ?? h.Value;
        }

        // 4. Apply auto-applied headers (Content-Type, etc.) — these don't need substitution.
        foreach (var h in snapshot.AutoAppliedHeaders.Where(h => h.IsEnabled))
        {
            if (!string.IsNullOrWhiteSpace(h.Key))
                headers[h.Key] = h.Value;
        }

        // 5. Apply auth headers (mirrors RequestTabViewModel.ApplyAuthHeaders).
        // Use EffectiveAuth when available so that inherited auth is correctly reflected.
        ApplyAuthHeaders(snapshot.EffectiveAuth ?? snapshot.Auth, headers, vars, ref requestUrl);

        // 6. Substitute any remaining tokens in the final URL.
        requestUrl = Substitute(requestUrl, vars) ?? requestUrl;

        // 7. Resolve body.
        string? resolvedBody = null;
        byte[]? resolvedBodyBytes = null;
        IReadOnlyList<KeyValuePair<string, string>>? multipartFormParams = null;
        IReadOnlyList<MultipartFilePart>? multipartFormFiles = null;

        switch (snapshot.BodyType)
        {
            case CollectionRequest.BodyTypes.None:
                break;

            case CollectionRequest.BodyTypes.Form when snapshot.FormParams.Count > 0:
            {
                var formPairs = snapshot.FormParams
                    .Select(p => new KeyValuePair<string, string>(
                        Substitute(p.Key, vars) ?? p.Key,
                        Substitute(p.Value, vars) ?? p.Value))
                    .ToList();
                resolvedBody = string.Join("&",
                    formPairs.Select(p =>
                        Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
                break;
            }

            case CollectionRequest.BodyTypes.Multipart when snapshot.FormParams.Count > 0:
                multipartFormParams = snapshot.FormParams
                    .Select(p => new KeyValuePair<string, string>(
                        Substitute(p.Key, vars) ?? p.Key,
                        Substitute(p.Value, vars) ?? p.Value))
                    .ToList();
                multipartFormFiles = ResolveMultipartFiles(snapshot.MultipartFormFiles, vars);
                break;

            case CollectionRequest.BodyTypes.Multipart when snapshot.MultipartFormFiles.Count > 0:
                multipartFormFiles = ResolveMultipartFiles(snapshot.MultipartFormFiles, vars);
                break;

            case CollectionRequest.BodyTypes.File when snapshot.FileBodyBase64 is not null:
                resolvedBodyBytes = Convert.FromBase64String(snapshot.FileBodyBase64);
                break;

            default:
                resolvedBody = string.IsNullOrEmpty(snapshot.Body)
                    ? null
                    : Substitute(snapshot.Body, vars);
                break;
        }

        // 8. Determine Content-Type from the snapshot body type.
        var contentType = CollectionRequest.BodyTypes.ToContentType(snapshot.BodyType);

        return new RequestModel
        {
            Method = new HttpMethod(snapshot.Method),
            Url = requestUrl,
            Headers = headers,
            Body = resolvedBody,
            BodyBytes = resolvedBodyBytes,
            MultipartFormParams = multipartFormParams,
            MultipartFormFiles = multipartFormFiles,
            ContentType = contentType,
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts a <see cref="VariableBinding"/> list into a plain name → value dictionary
    /// suitable for use with <see cref="VariableSubstitutionService.Substitute"/>.
    /// Strips the <c>{{ }}</c> braces from token names if present.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildVariableMap(
        IReadOnlyList<VariableBinding> bindings)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in bindings)
        {
            // Token may be stored as "{{name}}" or bare "name" — normalise to bare name.
            var name = b.Token.Length >= 4
                && b.Token.StartsWith("{{", StringComparison.Ordinal)
                && b.Token.EndsWith("}}", StringComparison.Ordinal)
                ? b.Token[2..^2].Trim()
                : b.Token.Trim();

            // Last-write wins for duplicate tokens — consistent with substitution order.
            map[name] = b.ResolvedValue;
        }
        return map;
    }

    private static string? Substitute(string? template, IReadOnlyDictionary<string, string> vars) =>
        VariableSubstitutionService.Substitute(template, vars);

    private static IReadOnlyList<MultipartFilePart> ResolveMultipartFiles(
        IReadOnlyList<MultipartFilePart> files,
        IReadOnlyDictionary<string, string> vars)
        => files
            .Where(f => f.IsEnabled)
            .Where(f => !string.IsNullOrWhiteSpace(f.Key))
            .Select(f => new MultipartFilePart
            {
                Key = Substitute(f.Key, vars) ?? f.Key,
                FileBytes = f.FileBytes,
                FileName = f.FileName,
                FilePath = f.FilePath,
                IsEnabled = true,
            })
            .ToList();

    /// <summary>
    /// Applies the auth configuration to <paramref name="headers"/> and <paramref name="url"/>
    /// using variable substitution from <paramref name="vars"/>.
    /// This is the canonical, non-collecting implementation of auth header injection — the same
    /// algorithm used at actual send time, without variable-binding collection.
    /// Used both internally by <see cref="Build"/> and by the cURL command preview.
    /// </summary>
    public static void ApplyAuthHeaders(
        AuthConfig auth,
        Dictionary<string, string> headers,
        IReadOnlyDictionary<string, string> vars,
        ref string url)
    {
        url = AuthHeaderApplier.Apply(auth, headers, vars, url);
    }
}
