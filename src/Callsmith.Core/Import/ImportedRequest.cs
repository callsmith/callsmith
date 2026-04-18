using System.Net.Http;
using Callsmith.Core.Models;

namespace Callsmith.Core.Import;

/// <summary>
/// A single HTTP request extracted from an external collection format
/// (Insomnia, Postman, etc.) during import. Format-agnostic.
/// </summary>
public sealed class ImportedRequest
{
    /// <summary>Display name of the request.</summary>
    public required string Name { get; init; }

    /// <summary>HTTP method (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS).</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>
    /// The request URL, which may contain <c>{{variableName}}</c> placeholders.
    /// The importer is responsible for normalizing the source tool's variable syntax
    /// to the Callsmith <c>{{name}}</c> convention.
    /// </summary>
    public required string Url { get; set; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Request headers. Items may be disabled (IsEnabled = false) — disabled headers from
    /// the source tool are preserved for import but not sent with the HTTP request.
    /// </summary>
    public IReadOnlyList<RequestKv> Headers { get; init; } = [];

    /// <summary>Body content type — mirrors <c>BodyTypes</c> constants in Core.</summary>
    public string BodyType { get; init; } = "none";

    /// <summary>Raw body content string. Null when <see cref="BodyType"/> is <c>"none"</c>.</summary>
    public string? Body { get; init; }

    /// <summary>
    /// Query parameters stored separately from the base URL.
    /// Duplicate keys are preserved. Items may be disabled (IsEnabled = false) — disabled
    /// params from the source tool are preserved for import.
    /// </summary>
    public IReadOnlyList<RequestKv> QueryParams { get; init; } = [];

    /// <summary>
    /// Path parameters keyed by placeholder name (e.g. <c>{id}</c> → value).
    /// </summary>
    public IReadOnlyDictionary<string, string> PathParams { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Authentication configuration translated from the source format.
    /// Defaults to <c>AuthConfig.AuthTypes.None</c> when the source request has no auth.
    /// </summary>
    public AuthConfig Auth { get; init; } = new();

    /// <summary>
    /// Form parameters for <c>application/x-www-form-urlencoded</c> bodies.
    /// Populated instead of <see cref="Body"/> when <see cref="BodyType"/> is "form".
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> FormParams { get; init; } = [];
}
