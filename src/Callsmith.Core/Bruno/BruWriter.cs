using System.Text;

namespace Callsmith.Core.Bruno;

/// <summary>
/// Serialises a sequence of <see cref="BruBlock"/> entries back to <c>.bru</c> text.
/// <para>
/// When a block carries <see cref="BruBlock.HasPrecedingBlankLine"/> = <c>true</c> (set by
/// <see cref="BruParser"/> during round-trip reads), the blank line before that block is
/// preserved.  For blocks that did <em>not</em> come from a parsed file (e.g. newly created
/// blocks inserted by Callsmith), a blank line is emitted by default so that the output
/// remains readable.
/// </para>
/// </summary>
internal static class BruWriter
{
    /// <summary>
    /// Renders <paramref name="blocks"/> to a Bruno-format string using the specified line ending.
    /// </summary>
    /// <param name="blocks">The blocks to render.</param>
    /// <param name="newLine">
    /// The line-ending sequence to use throughout the output (<c>"\n"</c> or <c>"\r\n"</c>).
    /// Pass <see cref="BruDocument.LineEnding"/> when doing a round-trip save so that the
    /// file's original line endings are preserved and git does not show spurious diffs.
    /// Defaults to <c>"\n"</c> (LF) for newly created files.
    /// </param>
    public static string Write(IEnumerable<BruBlock> blocks, string newLine = "\n")
    {
        var sb = new StringBuilder();
        var first = true;

        foreach (var block in blocks)
        {
            if (!first)
            {
                // Honour the blank-line flag captured during parsing so that round-trip
                // saves do not introduce blank lines that were not in the original file.
                // Newly-created blocks (HasPrecedingBlankLine = false on construction) that
                // are inserted via SetOrInsertAfter get the flag set to true so that they
                // still receive a separator for readability.
                if (block.HasPrecedingBlankLine)
                    sb.Append(newLine);
            }
            first = false;
            WriteBlock(sb, block, newLine);
        }

        return sb.ToString().TrimEnd('\r', '\n') + newLine;
    }

    private static void WriteBlock(StringBuilder sb, BruBlock block, string newLine)
    {
        if (ShouldWriteListBlock(block))
        {
            WriteListBlock(sb, block, newLine);
            return;
        }

        sb.Append(block.Name).Append(" {").Append(newLine);

        if (block.IsRaw)
        {
            if (!string.IsNullOrEmpty(block.RawContent))
            {
                // RawContent always stores newlines as "\n" (line endings are stripped by
                // StringReader.ReadLine during parsing).  Expand to the target line ending
                // so that files originally written with CRLF are written back with CRLF.
                var rawContent = string.Equals(newLine, "\n", StringComparison.Ordinal)
                    ? block.RawContent
                    : block.RawContent.Replace("\n", newLine);
                sb.Append(rawContent).Append(newLine);
            }
        }
        else
        {
            foreach (var kv in block.Items)
            {
                sb.Append("  ");
                if (!kv.IsEnabled) sb.Append('~');
                sb.Append(kv.Key).Append(": ").Append(kv.Value).Append(newLine);
            }
        }

        sb.Append("}").Append(newLine);
    }

    private static bool ShouldWriteListBlock(BruBlock block) =>
        string.Equals(block.Name, "vars:secret", StringComparison.Ordinal) &&
        block.Items.All(kv => string.IsNullOrEmpty(kv.Value));

    private static void WriteListBlock(StringBuilder sb, BruBlock block, string newLine)
    {
        sb.Append(block.Name).Append(" [").Append(newLine);

        for (var i = 0; i < block.Items.Count; i++)
        {
            var kv = block.Items[i];
            sb.Append("  ");
            if (!kv.IsEnabled) sb.Append('~');
            sb.Append(kv.Key);
            if (i < block.Items.Count - 1)
                sb.Append(',');
            sb.Append(newLine);
        }

        sb.Append("]").Append(newLine);
    }
}
