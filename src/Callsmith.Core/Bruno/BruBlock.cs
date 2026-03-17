namespace Callsmith.Core.Bruno;

/// <summary>
/// A single parsed block from a <c>.bru</c> file, e.g. <c>get</c>, <c>headers</c>,
/// <c>body:json</c>, or <c>script:pre-request</c>.
/// </summary>
internal sealed class BruBlock
{
    /// <summary>Block name as it appears in the file, e.g. <c>"get"</c>, <c>"body:json"</c>.</summary>
    public string Name { get; }

    /// <summary>Key-value items for non-raw blocks (everything except scripts and body blobs).</summary>
    public List<BruKv> Items { get; } = [];

    /// <summary>Verbatim text content for raw blocks (scripts, JSON/XML/text bodies).</summary>
    public string? RawContent { get; set; }

    /// <summary><c>true</c> when the block holds raw text rather than key-value pairs.</summary>
    public bool IsRaw => IsRawBlockName(Name);

    public BruBlock(string name) => Name = name;

    /// <summary>
    /// Returns the value for the first <em>enabled</em> item with the given key (case-insensitive),
    /// or <c>null</c> if not found.
    /// </summary>
    public string? GetValue(string key) =>
        Items.FirstOrDefault(kv =>
                kv.IsEnabled &&
                string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    /// <summary>Returns <c>true</c> for block names whose content is raw text, not key-value lines.</summary>
    internal static bool IsRawBlockName(string name) => name is
        "body:json" or "body:xml" or "body:text" or "body:bytes" or "body:graphql"
        or "script:pre-request" or "script:post-response" or "tests";
}
