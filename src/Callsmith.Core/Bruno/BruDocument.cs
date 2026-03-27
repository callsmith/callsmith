namespace Callsmith.Core.Bruno;

/// <summary>All blocks parsed from a single <c>.bru</c> file, in document order.</summary>
internal sealed class BruDocument
{
    /// <summary>All blocks in the order they appear in the source file.</summary>
    public List<BruBlock> Blocks { get; } = [];

    /// <summary>
    /// The line ending detected in the source file (<c>"\r\n"</c> or <c>"\n"</c>).
    /// Defaults to <c>"\n"</c> when the input contains no newline characters.
    /// Preserved by <see cref="BruWriter"/> so that round-trip saves do not alter the
    /// line endings of files that were originally written with CRLF.
    /// </summary>
    public string LineEnding { get; set; } = "\n";

    /// <summary>
    /// Returns the first block with the given name (case-insensitive), or <c>null</c>.
    /// </summary>
    public BruBlock? Find(string name) =>
        Blocks.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Convenience shortcut: gets a single enabled value from a named block.</summary>
    public string? GetValue(string blockName, string key) =>
        Find(blockName)?.GetValue(key);
}
