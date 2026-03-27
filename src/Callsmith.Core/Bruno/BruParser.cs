using System.Text;
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
/// <para>
/// The <c>vars:secret</c> block additionally supports list syntax: <c>vars:secret [name1, name2]</c>
/// for inline comma-separated variable names.
/// </para>
/// </summary>
internal static class BruParser
{
    // Matches a block header at column 0 with optional trailing whitespace before the brace.
    // Examples: "get {", "body:json {", "script:pre-request {", "params:query {"
    private static readonly Regex _blockHeaderRegex =
        new(@"^([\w][\w:.-]*)\s*\{$", RegexOptions.Compiled);

    // Matches list-based vars:secret block: "vars:secret [" with optional content
    private static readonly Regex _listBlockHeaderRegex =
        new(@"^(vars:secret)\s*\[", RegexOptions.Compiled);

    /// <summary>Parses <paramref name="text"/> and returns the resulting document.</summary>
    public static BruDocument Parse(string text)
    {
        var doc = new BruDocument();
        using var reader = new StringReader(text);

        string? line;
        var precedingBlankLine = false;
        while ((line = reader.ReadLine()) is not null)
        {
            // Track whether a blank line precedes the next block header.
            if (string.IsNullOrWhiteSpace(line))
            {
                precedingBlankLine = true;
                continue;
            }

            // Check for list-based vars:secret block first
            var listMatch = _listBlockHeaderRegex.Match(line);
            if (listMatch.Success)
            {
                var block = new BruBlock(listMatch.Groups[1].Value) { HasPrecedingBlankLine = precedingBlankLine };
                // Extract content after the opening bracket
                var afterBracket = line[(line.IndexOf('[') + 1)..];
                ReadListBlockContent(reader, block, afterBracket);
                doc.Blocks.Add(block);
                precedingBlankLine = false;
                continue;
            }

            // Check for regular key-value block
            var m = _blockHeaderRegex.Match(line);
            if (!m.Success)
            {
                precedingBlankLine = false;
                continue;
            }

            var kvBlock = new BruBlock(m.Groups[1].Value) { HasPrecedingBlankLine = precedingBlankLine };
            ReadBlockContent(reader, kvBlock);
            doc.Blocks.Add(kvBlock);
            precedingBlankLine = false;
        }

        return doc;
    }

    /// <summary>Reads content from a list-based block (e.g., vars:secret [ item1, item2 ]).</summary>
    private static void ReadListBlockContent(StringReader reader, BruBlock block, string initialContent)
    {
        var itemsText = new StringBuilder();
        
        // Check if initialContent contains the closing bracket
        var closingBracketIndex = initialContent.IndexOf(']');
        if (closingBracketIndex >= 0)
        {
            // Content is inline, ends here
            itemsText.Append(initialContent[..closingBracketIndex]);
        }
        else
        {
            // Content continues on next lines
            itemsText.Append(initialContent);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine == "]" || trimmedLine.EndsWith(']'))
                {
                    // If there's content before the ], add it
                    if (trimmedLine.Length > 1 && trimmedLine != "]")
                        itemsText.Append(' ').Append(trimmedLine[..^1]); // Everything before the ]
                    break;
                }

                if (!string.IsNullOrEmpty(trimmedLine))
                    itemsText.Append(' ').Append(trimmedLine);
            }
        }

        // Parse the collected text as comma-separated names
        var text = itemsText.ToString().Trim();
        if (string.IsNullOrEmpty(text))
            return;

        // Split by commas and create BruKv items with empty values
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in items)
        {
            var trimmed = item.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                var isEnabled = !trimmed.StartsWith('~');
                var itemName = isEnabled ? trimmed : trimmed[1..].Trim();
                block.Items.Add(new BruKv(itemName, string.Empty, isEnabled));
            }
        }
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
            block.RawContent = string.Join('\n', rawLines);
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
