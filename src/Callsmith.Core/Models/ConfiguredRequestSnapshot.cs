using System.Net.Http;

namespace Callsmith.Core.Models;

/// <summary>
/// Immutable snapshot of the complete request configuration captured at the moment
/// the user pressed Send — before any variable substitution ran.
/// <para>
/// Contains user-authored fields from the saved <see cref="CollectionRequest"/> (with
/// <c>{{token}}</c> placeholders intact) plus any headers the application injected
/// automatically at send time (e.g. <c>Content-Type</c>).
/// </para>
/// <para>
/// Together with a <see cref="VariableBinding"/> list this is sufficient to reconstruct
/// the exact wire-level ("Resolved") view without a live environment.
/// </para>
/// </summary>
public sealed class ConfiguredRequestSnapshot
{
    /// <summary>HTTP method string, e.g. "GET", "POST".</summary>
    public required string Method { get; init; }

    /// <summary>
    /// URL template, may contain <c>{{variable}}</c> placeholders.
    /// Does not include query parameters.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// User-authored request headers (enabled and disabled).
    /// Values may contain <c>{{variable}}</c> placeholders.
    /// </summary>
    public IReadOnlyList<RequestKv> Headers { get; init; } = [];

    /// <summary>
    /// Headers the application applied automatically at send time — for example,
    /// the <c>Content-Type</c> derived from the chosen body type. These are distinct from
    /// user-authored headers so the UI can display them separately in the "Configured" view.
    /// </summary>
    public IReadOnlyList<RequestKv> AutoAppliedHeaders { get; init; } = [];

    /// <summary>
    /// Query parameters stored separately from the base URL.
    /// Values may contain <c>{{variable}}</c> placeholders.
    /// </summary>
    public IReadOnlyList<RequestKv> QueryParams { get; init; } = [];

    /// <summary>
    /// Path parameter values keyed by placeholder name.
    /// Values may contain <c>{{variable}}</c> placeholders.
    /// </summary>
    public IReadOnlyDictionary<string, string> PathParams { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Body type constant (json, text, xml, form, none, …).</summary>
    public string BodyType { get; init; } = CollectionRequest.BodyTypes.None;

    /// <summary>Raw body text. May contain <c>{{variable}}</c> placeholders. Null for bodyless types.</summary>
    public string? Body { get; init; }

    /// <summary>Form parameters for form-encoded bodies. Values may contain placeholders.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> FormParams { get; init; } = [];

    /// <summary>
    /// File parameters for multipart/form-data bodies.
    /// </summary>
    public IReadOnlyList<MultipartFilePart> MultipartFormFiles { get; init; } = [];

    /// <summary>
    /// Combined ordered list of all multipart body parts (text fields and file fields) in the
    /// order the user arranged them in the editor.
    /// </summary>
    public IReadOnlyList<MultipartBodyEntry> MultipartBodyEntries { get; init; } = [];

    /// <summary>
    /// Binary file body encoded as Base64.
    /// Only populated when <see cref="BodyType"/> is <see cref="CollectionRequest.BodyTypes.File"/>.
    /// </summary>
    public string? FileBodyBase64 { get; init; }

    /// <summary>
    /// Original file name of the uploaded file.
    /// Only populated when <see cref="BodyType"/> is <see cref="CollectionRequest.BodyTypes.File"/>.
    /// </summary>
    public string? FileBodyName { get; init; }

    /// <summary>Authentication configuration. Field values may contain <c>{{variable}}</c> placeholders.</summary>
    public AuthConfig Auth { get; init; } = new();

    /// <summary>
    /// The effective authentication configuration that was actually applied when the request was sent.
    /// When <see cref="Auth"/> has <see cref="AuthConfig.AuthTypes.Inherit"/>, this contains the
    /// resolved inherited auth walked up from the parent folder hierarchy; otherwise it mirrors
    /// <see cref="Auth"/>. Field values may contain <c>{{variable}}</c> placeholders.
    /// <para>
    /// Use this (with <see cref="Auth"/> as a fallback for older entries) when reconstructing the
    /// exact wire-level request, e.g. in <see cref="HistorySentViewBuilder"/>.
    /// </para>
    /// </summary>
    public AuthConfig? EffectiveAuth { get; init; }

    /// <summary>
    /// Creates a <see cref="ConfiguredRequestSnapshot"/> from a <see cref="CollectionRequest"/>
    /// with additional auto-applied headers provided by the caller.
    /// </summary>
    public static ConfiguredRequestSnapshot FromCollectionRequest(
        CollectionRequest request,
        IReadOnlyList<RequestKv>? autoAppliedHeaders = null) =>
        new()
        {
            Method = request.Method.Method,
            Url = request.Url,
            Headers = request.Headers,
            AutoAppliedHeaders = autoAppliedHeaders ?? [],
            QueryParams = request.QueryParams,
            PathParams = request.PathParams,
            BodyType = request.BodyType,
            Body = request.Body,
            FormParams = request.FormParams,
            MultipartFormFiles = request.MultipartFormFiles,
            MultipartBodyEntries = request.MultipartBodyEntries,
            FileBodyBase64 = request.FileBodyBase64,
            FileBodyName = request.FileBodyName,
            Auth = request.Auth,
        };
}
