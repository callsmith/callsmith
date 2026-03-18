using YamlDotNet.Serialization;

namespace Callsmith.Core.Insomnia;

// ─────────────────────────────────────────────────────────────────────────────
// DTOs for Insomnia collection format v5 (schema_version 5.x).
// These are internal deserialization-only types — not part of the public API.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class InsomniaDocument
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [YamlMember(Alias = "schema_version")]
    public string SchemaVersion { get; set; } = string.Empty;

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "collection")]
    public List<InsomniaCollectionItem> Collection { get; set; } = [];

    [YamlMember(Alias = "environments")]
    public InsomniaEnvironmentBlock? Environments { get; set; }
}

/// <summary>
/// Represents either a request or a folder (the discriminator is the presence of 'url' vs 'children').
/// </summary>
internal sealed class InsomniaCollectionItem
{
    // ── Shared ──
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "meta")]
    public InsomniaItemMeta? Meta { get; set; }

    // ── Request-only fields ──
    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    [YamlMember(Alias = "method")]
    public string? Method { get; set; }

    [YamlMember(Alias = "headers")]
    public List<InsomniaHeader>? Headers { get; set; }

    [YamlMember(Alias = "body")]
    public InsomniaBody? Body { get; set; }

    [YamlMember(Alias = "pathParameters")]
    public List<InsomniaPathParam>? PathParameters { get; set; }

    [YamlMember(Alias = "parameters")]
    public List<InsomniaQueryParam>? Parameters { get; set; }

    [YamlMember(Alias = "authentication")]
    public InsomniaAuthentication? Authentication { get; set; }

    // ── Folder-only fields ──
    [YamlMember(Alias = "children")]
    public List<InsomniaCollectionItem>? Children { get; set; }

    /// <summary>True when this item represents a request (has a URL).</summary>
    public bool IsRequest => Url != null;
}

internal sealed class InsomniaItemMeta
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    [YamlMember(Alias = "sortKey")]
    public double SortKey { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }
}

internal sealed class InsomniaHeader
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "value")]
    public string Value { get; set; } = string.Empty;

    [YamlMember(Alias = "disabled")]
    public bool Disabled { get; set; }
}

internal sealed class InsomniaBody
{
    [YamlMember(Alias = "mimeType")]
    public string? MimeType { get; set; }

    [YamlMember(Alias = "text")]
    public string? Text { get; set; }

    [YamlMember(Alias = "params")]
    public List<InsomniaFormParam>? Params { get; set; }
}

internal sealed class InsomniaFormParam
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "value")]
    public string Value { get; set; } = string.Empty;

    [YamlMember(Alias = "disabled")]
    public bool Disabled { get; set; }
}

internal sealed class InsomniaPathParam
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "value")]
    public string Value { get; set; } = string.Empty;
}

internal sealed class InsomniaQueryParam
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "value")]
    public string Value { get; set; } = string.Empty;

    [YamlMember(Alias = "disabled")]
    public bool Disabled { get; set; }
}

internal sealed class InsomniaAuthentication
{
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    // Bearer
    [YamlMember(Alias = "token")]
    public string? Token { get; set; }

    // Basic
    [YamlMember(Alias = "username")]
    public string? Username { get; set; }

    [YamlMember(Alias = "password")]
    public string? Password { get; set; }

    // API Key
    [YamlMember(Alias = "key")]
    public string? Key { get; set; }

    [YamlMember(Alias = "value")]
    public string? Value { get; set; }

    /// <summary>"header" or "queryParams"</summary>
    [YamlMember(Alias = "addTo")]
    public string? AddTo { get; set; }
}

internal sealed class InsomniaEnvironmentBlock
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "data")]
    public Dictionary<string, string>? Data { get; set; }

    [YamlMember(Alias = "subEnvironments")]
    public List<InsomniaSubEnvironment>? SubEnvironments { get; set; }
}

internal sealed class InsomniaSubEnvironment
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "data")]
    public Dictionary<string, string>? Data { get; set; }

    [YamlMember(Alias = "color")]
    public string? Color { get; set; }

    [YamlMember(Alias = "meta")]
    public InsomniaSubEnvMeta? Meta { get; set; }
}

internal sealed class InsomniaSubEnvMeta
{
    [YamlMember(Alias = "sortKey")]
    public double SortKey { get; set; }
}
