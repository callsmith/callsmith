using System.Net.Http;
using Callsmith.Core.Helpers;

namespace Callsmith.Core.Models;

/// <summary>
/// Represents a single saved request as it exists on disk in a collection folder.
/// This is the domain model — it is distinct from <see cref="RequestModel"/>,
/// which is the transport-layer representation used when actually sending a request.
/// </summary>
public sealed class CollectionRequest
{
    /// <summary>
    /// Stable identity for this request that persists across renames and moves.
    /// Generated on first save for requests created after this field was introduced;
    /// <see langword="null"/> only for requests loaded from old files that have never
    /// been saved since the upgrade (lazily assigned on next save).
    /// </summary>
    public Guid? RequestId { get; init; }

    /// <summary>
    /// The file path on disk where this request is stored.
    /// Used as the stable identity for load/save operations.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>Display name for this request, derived from the filename (without extension).</summary>
    public required string Name { get; init; }

    /// <summary>The HTTP method (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS).</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>The request URL, which may contain <c>{{variableName}}</c> placeholders.</summary>
    public required string Url { get; init; }

    /// <summary>Optional human-readable description of the request.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Request headers. Items may be disabled (IsEnabled = false) — disabled headers are
    /// preserved on disk but not sent with the HTTP request.
    /// </summary>
    public IReadOnlyList<RequestKv> Headers { get; init; } = [];

    /// <summary>The body content type (e.g. "json", "text", "xml", "form", "multipart", "none").</summary>
    public string BodyType { get; init; } = BodyTypes.None;

    /// <summary>Raw body content. Null when <see cref="BodyType"/> is <c>"none"</c>.</summary>
    public string? Body { get; init; }

    /// <summary>
    /// Body content for every text-based body type that was present in the source file,
    /// regardless of which type is currently active.  Keys are <see cref="BodyTypes"/> constants
    /// (<c>"json"</c>, <c>"text"</c>, <c>"xml"</c>).  Used by the UI to restore the editor
    /// content when the user switches body types without saving in between.
    /// </summary>
    public IReadOnlyDictionary<string, string> AllBodyContents { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Query parameters stored separately from the base URL.
    /// Duplicate keys are preserved. Items may be disabled (IsEnabled = false) — disabled
    /// params are preserved on disk but not appended to the URL when sending.
    /// </summary>
    public IReadOnlyList<RequestKv> QueryParams { get; init; } = [];

    /// <summary>
    /// Path parameters keyed by placeholder name (for URL templates such as <c>/users/{id}</c>).
    /// Values are substituted into the URL path at send time.
    /// </summary>
    public IReadOnlyDictionary<string, string> PathParams { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Authentication configuration for this request.</summary>
    public AuthConfig Auth { get; init; } = new();

    /// <summary>
    /// Form parameters for <c>application/x-www-form-urlencoded</c> or <c>multipart/form-data</c> bodies.
    /// Populated instead of <see cref="Body"/> when <see cref="BodyType"/> is "form" or "multipart".
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> FormParams { get; init; } = [];

    /// <summary>
    /// File parameters for <c>multipart/form-data</c> bodies.
    /// </summary>
    public IReadOnlyList<MultipartFilePart> MultipartFormFiles { get; init; } = [];

    /// <summary>
    /// Binary file body encoded as a Base64 string.
    /// Only populated when <see cref="BodyType"/> is <c>"file"</c>.
    /// </summary>
    public string? FileBodyBase64 { get; init; }

    /// <summary>
    /// The original file name of the uploaded file (e.g. <c>"image.png"</c>).
    /// Only populated when <see cref="BodyType"/> is <c>"file"</c>.
    /// </summary>
    public string? FileBodyName { get; init; }

    /// <summary>
    /// The full URL including all <em>enabled</em> query parameters from <see cref="QueryParams"/>.
    /// Use this when building a <c>RequestModel</c> to send.
    /// </summary>
    public string FullUrl
    {
        get
        {
            var enabled = QueryParams
                .Where(p => p.IsEnabled)
                .Select(p => new KeyValuePair<string, string>(p.Key, p.Value))
                .ToList();
            return enabled.Count > 0 ? QueryStringHelper.AppendQueryParams(Url, enabled) : Url;
        }
    }

    /// <summary>Well-known body type constants to avoid magic strings.</summary>
    public static class BodyTypes
    {
        public const string None = "none";
        public const string Json = "json";
        public const string Text = "text";
        public const string Xml = "xml";
        public const string Yaml = "yaml";
        public const string Other = "other";
        public const string Form = "form";
        public const string Multipart = "multipart";
        public const string File = "file";

        // MIME content-type values that map to each body type token.
        public const string JsonContentType = "application/json";
        public const string TextContentType = "text/plain";
        public const string XmlContentType  = "application/xml";
        public const string YamlContentType = "application/yaml";
        public const string FileContentType = "application/octet-stream";

        /// <summary>
        /// Returns the MIME content type for the given body type token,
        /// or <see langword="null"/> when no content-type should be set.
        /// </summary>
        public static string? ToContentType(string? bodyType) => bodyType switch
        {
            Json      => JsonContentType,
            Text      => TextContentType,
            Xml       => XmlContentType,
            Yaml      => YamlContentType,
            File      => FileContentType,
            Form      => "application/x-www-form-urlencoded",
            Multipart => "multipart/form-data",
            _         => null,
        };
    }
}
