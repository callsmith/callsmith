using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callsmith.Core.Postman;

// ─────────────────────────────────────────────────────────────────────────────
// DTOs for Postman Collection Format v2.0 / v2.1.
// Internal deserialization-only types — not part of the public API.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class PostmanDocument
{
    [JsonPropertyName("info")]
    public PostmanInfo Info { get; set; } = new();

    /// <summary>Top-level items — may be folders (have nested "item" arrays) or requests.</summary>
    [JsonPropertyName("item")]
    public List<PostmanItem> Item { get; set; } = [];

    /// <summary>Collection-level variables.</summary>
    [JsonPropertyName("variable")]
    public List<PostmanVariable>? Variable { get; set; }

    /// <summary>Collection-level auth. Applies to all requests unless overridden.</summary>
    [JsonPropertyName("auth")]
    public PostmanAuth? Auth { get; set; }

    /// <summary>Collection-level event hooks (pre-request / test scripts). Ignored on import.</summary>
    [JsonPropertyName("event")]
    public List<PostmanEvent>? Event { get; set; }
}

internal sealed class PostmanInfo
{
    [JsonPropertyName("_postman_id")]
    public string PostmanId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("schema")]
    public string Schema { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Represents either a folder (has a nested <see cref="Item"/> array)
/// or a request (has a non-null <see cref="Request"/>).
/// </summary>
internal sealed class PostmanItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Non-null when this item is a request.</summary>
    [JsonPropertyName("request")]
    public PostmanRequest? Request { get; set; }

    /// <summary>Non-null (and non-empty) when this item is a folder.</summary>
    [JsonPropertyName("item")]
    public List<PostmanItem>? Item { get; set; }

    /// <summary>Item-level event hooks (pre-request / test scripts). Ignored on import.</summary>
    [JsonPropertyName("event")]
    public List<PostmanEvent>? Event { get; set; }

    /// <summary>Description attached to this item.</summary>
    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }

    /// <summary>True when this item is a request (leaf node).</summary>
    public bool IsRequest => Request is not null;
}

internal sealed class PostmanRequest
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("header")]
    public List<PostmanHeader>? Header { get; set; }

    [JsonPropertyName("body")]
    public PostmanBody? Body { get; set; }

    /// <summary>
    /// URL — can be a plain string <em>or</em> a structured object.
    /// Use a <see cref="JsonElement"/> and inspect the kind at runtime.
    /// </summary>
    [JsonPropertyName("url")]
    public JsonElement Url { get; set; }

    [JsonPropertyName("auth")]
    public PostmanAuth? Auth { get; set; }

    /// <summary>Description — can be a plain string or a structured object with a "content" field.</summary>
    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }

    /// <summary>
    /// When true, Postman will not prune the body from methods that normally have no body
    /// (GET, HEAD, DELETE). Informational only; does not affect import.
    /// </summary>
    [JsonPropertyName("protocolProfileBehavior")]
    public JsonElement? ProtocolProfileBehavior { get; set; }
}

internal sealed class PostmanUrl
{
    [JsonPropertyName("raw")]
    public string? Raw { get; set; }

    [JsonPropertyName("host")]
    public List<string>? Host { get; set; }

    [JsonPropertyName("path")]
    public List<JsonElement>? Path { get; set; }

    [JsonPropertyName("query")]
    public List<PostmanQueryParam>? Query { get; set; }

    /// <summary>Path parameter values — keyed by variable name.</summary>
    [JsonPropertyName("variable")]
    public List<PostmanVariable>? Variable { get; set; }
}

internal sealed class PostmanQueryParam
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; set; }

    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }
}

internal sealed class PostmanHeader
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; set; }

    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }
}

internal sealed class PostmanBody
{
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>Raw body content string (for mode = "raw").</summary>
    [JsonPropertyName("raw")]
    public string? Raw { get; set; }

    [JsonPropertyName("formdata")]
    public List<PostmanFormParam>? Formdata { get; set; }

    [JsonPropertyName("urlencoded")]
    public List<PostmanFormParam>? Urlencoded { get; set; }

    /// <summary>Language/media-type options for raw bodies.</summary>
    [JsonPropertyName("options")]
    public PostmanBodyOptions? Options { get; set; }

    /// <summary>GraphQL body (object with "query" and optional "variables").</summary>
    [JsonPropertyName("graphql")]
    public PostmanGraphQLBody? GraphQL { get; set; }
}

internal sealed class PostmanBodyOptions
{
    [JsonPropertyName("raw")]
    public PostmanRawOptions? Raw { get; set; }
}

internal sealed class PostmanRawOptions
{
    /// <summary>Language hint: "json", "javascript", "xml", "html", "text".</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }
}

internal sealed class PostmanFormParam
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>"text" or "file".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; set; }

    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }
}

internal sealed class PostmanGraphQLBody
{
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("variables")]
    public string? Variables { get; set; }
}

internal sealed class PostmanAuth
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("bearer")]
    public List<PostmanAuthKv>? Bearer { get; set; }

    [JsonPropertyName("basic")]
    public List<PostmanAuthKv>? Basic { get; set; }

    [JsonPropertyName("apikey")]
    public List<PostmanAuthKv>? Apikey { get; set; }
}

internal sealed class PostmanAuthKv
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

internal sealed class PostmanVariable
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }
}

/// <summary>Postman event hook (pre-request / test script). Used for detection only; content is ignored.</summary>
internal sealed class PostmanEvent
{
    [JsonPropertyName("listen")]
    public string? Listen { get; set; }

    [JsonPropertyName("script")]
    public JsonElement? Script { get; set; }
}
