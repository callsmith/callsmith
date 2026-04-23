namespace Callsmith.Core.Models;

/// <summary>
/// Parsed representation of a cURL command that can be mapped into a request editor state.
/// </summary>
public sealed class ParsedCurlRequest
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<RequestKv> Headers { get; init; } = [];
    public IReadOnlyList<RequestKv> QueryParams { get; init; } = [];
    public string BodyType { get; init; } = CollectionRequest.BodyTypes.None;
    public string? Body { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> FormParams { get; init; } = [];
    public IReadOnlyList<MultipartFilePart> MultipartFormFiles { get; init; } = [];
    public AuthConfig Auth { get; init; } = new() { AuthType = AuthConfig.AuthTypes.None };
}
