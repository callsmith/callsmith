namespace Callsmith.Core.Models;

/// <summary>
/// A transport-agnostic description of a response received from a remote endpoint.
/// All transports produce this as output from <see cref="Abstractions.ITransport.SendAsync"/>.
/// </summary>
public sealed class ResponseModel
{
    /// <summary>HTTP status code (e.g. 200, 404, 500).</summary>
    public required int StatusCode { get; init; }

    /// <summary>HTTP reason phrase (e.g. "OK", "Not Found").</summary>
    public required string ReasonPhrase { get; init; }

    /// <summary>Response headers. May be empty but never null.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Response body decoded as a UTF-8 string. Empty string when there is no body.</summary>
    public required string Body { get; init; }

    /// <summary>Raw response body bytes. Empty array when there is no body.</summary>
    public required byte[] BodyBytes { get; init; }

    /// <summary>The final URL after any redirects were followed.</summary>
    public required string FinalUrl { get; init; }

    /// <summary>Total elapsed time from sending the request to receiving the full response.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Size of the response body in bytes.</summary>
    public long BodySizeBytes => BodyBytes.Length;
}
