using Callsmith.Core.Models;
using Callsmith.Core.MockData;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Assembles a complete RequestModel from request editor state and environment context.
/// Handles variable substitution, auth header application, body resolution, and all
/// header/URL/body transformations needed for sending.
/// </summary>
public interface IRequestAssemblyService
{
    /// <summary>
    /// Assembles a complete RequestModel from request editor configuration and environment state.
    /// </summary>
    /// <param name="request">Request editor configuration (method, URL, headers, body, auth, params).</param>
    /// <param name="globalEnvironment">Global environment variables.</param>
    /// <param name="activeEnvironment">Active collection-level environment (may be null).</param>
    /// <param name="collectionRootPath">Root folder path of the collection (used for environment merging and cache scope).</param>
    /// <param name="requestFilePath">Absolute path to the request file (used for inherited auth resolution).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An assembled and ready-to-send RequestModel with all variable substitution,
    /// auth headers, and body resolution applied. Also returns variable bindings
    /// captured during assembly for history recording.
    /// </returns>
    Task<AssembledRequest> AssembleAsync(
        RequestAssemblyInput request,
        EnvironmentModel globalEnvironment,
        EnvironmentModel? activeEnvironment,
        string collectionRootPath,
        string requestFilePath,
        CancellationToken ct = default);
}

/// <summary>
/// Input configuration for request assembly: the state of all request editor fields.
/// </summary>
public class RequestAssemblyInput
{
    /// <summary>HTTP method (GET, POST, etc.).</summary>
    public required string Method { get; set; }

    /// <summary>Base URL including path template placeholders.</summary>
    public required string Url { get; set; }

    /// <summary>Enabled header pairs from the headers editor.</summary>
    public required IEnumerable<KeyValuePair<string, string>> Headers { get; set; }

    /// <summary>Enabled path parameter pairs (substituted into URL path template).</summary>
    public required IEnumerable<KeyValuePair<string, string>> PathParams { get; set; }

    /// <summary>Enabled query parameter pairs (appended to URL).</summary>
    public required IEnumerable<KeyValuePair<string, string>> QueryParams { get; set; }

    /// <summary>Body type (None, Json, Form, Multipart, File, Xml, Yaml, Text, Other).</summary>
    public required string BodyType { get; set; }

    /// <summary>Root body text content (for non-form, non-file body types).</summary>
    public string? BodyText { get; set; }

    /// <summary>Form parameters (used for BodyType = Form or Multipart).</summary>
    public required IEnumerable<KeyValuePair<string, string>> FormParams { get; set; }

    /// <summary>Multipart file parameters (used for BodyType = Multipart).</summary>
    public IEnumerable<MultipartFilePart> MultipartFormFiles { get; set; } = [];

    /// <summary>File body bytes (when BodyType = File).</summary>
    public byte[]? FileBodyBytes { get; set; }

    /// <summary>Original filename for file body (metadata only).</summary>
    public string? FileBodyName { get; set; }

    /// <summary>Authentication configuration for the request.</summary>
    public required AuthConfig Auth { get; set; }

    /// <summary>Optional mock data generators for variable substitution.</summary>
    public IReadOnlyDictionary<string, MockDataEntry>? MockGenerators { get; set; }
}

/// <summary>
/// Output of request assembly: the fully prepared RequestModel plus metadata for history recording.
/// </summary>
public class AssembledRequest
{
    /// <summary>The ready-to-send RequestModel with all variables substituted and auth applied.</summary>
    public required RequestModel RequestModel { get; set; }

    /// <summary>
    /// The final resolved URL (after auth-based query param injection and variable substitution).
    /// May differ from RequestModel.Url if auth added query parameters.
    /// </summary>
    public required string ResolvedUrl { get; set; }

    /// <summary>
    /// Variable bindings discovered during assembly (for history recording and audit).
    /// Includes both sent-time bindings and collected references from all request fields.
    /// </summary>
    public required IReadOnlyList<VariableBinding> VariableBindings { get; set; }

    /// <summary>
    /// Effective auth configuration that was actually used for sending
    /// (after resolving inherited auth if applicable).
    /// </summary>
    public required AuthConfig EffectiveAuth { get; set; }

    /// <summary>
    /// Headers that were automatically applied (e.g., Content-Type for form bodies).
    /// Separated from explicit headers for history clarity.
    /// </summary>
    public required IReadOnlyList<RequestKv> AutoAppliedHeaders { get; set; }
}
