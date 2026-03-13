using System.Net.Http;

namespace Callsmith.Core.Models;

/// <summary>
/// Represents a single saved request as it exists on disk in a collection folder.
/// This is the domain model — it is distinct from <see cref="RequestModel"/>,
/// which is the transport-layer representation used when actually sending a request.
/// </summary>
public sealed class CollectionRequest
{
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

    /// <summary>Request headers. May be empty but never null.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    /// <summary>The body content type (e.g. "json", "text", "xml", "form", "multipart", "none").</summary>
    public string BodyType { get; init; } = BodyTypes.None;

    /// <summary>Raw body content. Null when <see cref="BodyType"/> is <c>"none"</c>.</summary>
    public string? Body { get; init; }

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
