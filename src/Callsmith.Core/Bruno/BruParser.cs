using System.Text.RegularExpressions;

namespace Callsmith.Core.Bruno;

/// <summary>
/// Parses <c>.bru</c> file text into a <see cref="BruDocument"/>.
/// <para>
/// The Bruno format is a sequence of named blocks delimited by <c>{ … }</c> where the
/// opening brace follows the block name on the same line and the closing brace must appear
/// alone on a line at column 0 (no leading whitespace).  Block content is either a set of
/// <c>key: value</c> lines (indented with two spaces) or verbatim raw text for script and
/// body blocks.
/// </para>
/// </summary>
internal static class BruParser
{
    // Matches a block header at column 0 with optional trailing whitespace before the brace.
    // Examples: "get {", "body:json {", "script:pre-request {", "params:query {"
    private static readonly Regex _blockHeaderRegex =
        new(@"^([\w][\w:.-]*)\s*\{$", RegexOptions.Compiled);

    /// <summary>Parses <paramref name="text"/> and returns the resulting document.</summary>
    public static BruDocument Parse(string text)
    {
        var doc = new BruDocument();
        using var reader = new StringReader(text);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var m = _blockHeaderRegex.Match(line);
            if (!m.Success) continue;

            var block = new BruBlock(m.Groups[1].Value);
            ReadBlockContent(reader, block);
            doc.Blocks.Add(block);
        }

        return doc;
    }

    private static void ReadBlockContent(StringReader reader, BruBlock block)
    {
        string? line;
        var rawLines = block.IsRaw ? new List<string>() : null;

        while ((line = reader.ReadLine()) is not null)
        {
            // A lone "}" at column 0 terminates the block.
            if (line == "}")
                break;

            if (rawLines is not null)
                rawLines.Add(line);
            else
                ParseKvLine(line, block);
        }

        if (rawLines is not null)
            block.RawContent = string.Join('\n', rawLines).TrimEnd();
    }

    private static void ParseKvLine(string line, BruBlock block)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        var isEnabled = !trimmed.StartsWith('~');
        var kvText = isEnabled ? trimmed : trimmed[1..];

        // Value is everything after the FIRST ": " (colon + space).
        var colon = kvText.IndexOf(": ", StringComparison.Ordinal);
        if (colon >= 0)
        {
            block.Items.Add(new BruKv(
                kvText[..colon].Trim(),
                kvText[(colon + 2)..],
                isEnabled));
            return;
        }

        // Fallback: bare "key:" with an empty value.
        colon = kvText.IndexOf(':');
        if (colon >= 0)
            block.Items.Add(new BruKv(kvText[..colon].Trim(), string.Empty, isEnabled));
    }
}
