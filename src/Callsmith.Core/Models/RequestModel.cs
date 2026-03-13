using System.Net.Http;

namespace Callsmith.Core.Models;

/// <summary>
/// A transport-agnostic description of an outgoing request.
/// All transports receive this as input to <see cref="Abstractions.ITransport.SendAsync"/>.
/// </summary>
public sealed class RequestModel
{
    /// <summary>The HTTP method (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS).</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>The fully-qualified request URL, including any query string.</summary>
    public required string Url { get; init; }

    /// <summary>Request headers. May be empty but never null.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Raw request body. Null for bodyless methods (GET, HEAD, DELETE, OPTIONS).</summary>
    public string? Body { get; init; }

    /// <summary>
    /// Content-Type of the body (e.g. "application/json"). Null when <see cref="Body"/> is null.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Per-request timeout. When null the transport uses its default timeout.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Whether the transport should follow HTTP redirects automatically.
    /// Defaults to true.
    /// </summary>
    public bool FollowRedirects { get; init; } = true;
}
