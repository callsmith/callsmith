namespace Callsmith.Core.Models;

/// <summary>
/// Immutable snapshot of the server response captured as part of a <see cref="HistoryEntry"/>.
/// Mirrors the data available in <see cref="ResponseModel"/> but is serialisation-friendly
/// (no byte arrays stored directly in the entity).
/// </summary>
public sealed class ResponseSnapshot
{
    /// <summary>HTTP status code, e.g. 200, 404.</summary>
    public required int StatusCode { get; init; }

    /// <summary>HTTP reason phrase, e.g. "OK", "Not Found".</summary>
    public required string ReasonPhrase { get; init; }

    /// <summary>Response headers at the time the response was received.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Response body decoded as UTF-8. Empty string when there was no body.
    /// Large bodies may be truncated by the repository (configurable in a later phase).
    /// </summary>
    public required string Body { get; init; }

    /// <summary>The final URL after any redirects were followed.</summary>
    public required string FinalUrl { get; init; }

    /// <summary>Size of the response body in bytes before any truncation.</summary>
    public required long BodySizeBytes { get; init; }

    /// <summary>Total elapsed time in milliseconds from send to full response.</summary>
    public required long ElapsedMs { get; init; }

    /// <summary>Creates a <see cref="ResponseSnapshot"/> from a live <see cref="ResponseModel"/>.</summary>
    public static ResponseSnapshot FromResponseModel(ResponseModel response) =>
        new()
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Headers = response.Headers,
            Body = response.Body,
            FinalUrl = response.FinalUrl,
            BodySizeBytes = response.BodySizeBytes,
            ElapsedMs = (long)response.Elapsed.TotalMilliseconds,
        };
}
