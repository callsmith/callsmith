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
    /// Response captures declared in a Bruno <c>vars:post-response</c> block.
    /// Each entry maps a variable name to a Bruno extraction expression (e.g. <c>res.body.token</c>).
    /// <para>
    /// Only populated when loading a request from a Bruno <c>.bru</c> file.  After a request
    /// completes, <c>BrunoPostResponseCaptureHelper.Apply</c> evaluates each expression against
    /// the response body and writes the extracted values into the currently active environment
    /// as plain static variable values.
    /// </para>
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> BrunoPostResponseCaptures { get; init; } = [];

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
            return enabled.Count > 0 ? QueryStringHelper.ApplyQueryParams(Url, enabled) : Url;
        }
    }

    /// <summary>Well-known body type constants to avoid magic strings.</summary>
    public static class BodyTypes
    {
        public const string None = "none";
        public const string Json = "json";
        public const string Text = "text";
        public const string Xml = "xml";
        public const string Form = "form";
        public const string Multipart = "multipart";
    }
}
