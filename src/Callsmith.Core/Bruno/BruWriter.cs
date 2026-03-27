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
    /// Renders <paramref name="blocks"/> to a Bruno-format string.
    /// </summary>
    public static string Write(IEnumerable<BruBlock> blocks)
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
                    sb.AppendLine();
            }
            first = false;
            WriteBlock(sb, block);
        }

        return sb.ToString().TrimEnd('\r', '\n') + "\n";
    }

    private static void WriteBlock(StringBuilder sb, BruBlock block)
    {
        if (ShouldWriteListBlock(block))
        {
            WriteListBlock(sb, block);
            return;
        }

        sb.Append(block.Name).AppendLine(" {");

        if (block.IsRaw)
        {
            if (!string.IsNullOrEmpty(block.RawContent))
                sb.AppendLine(block.RawContent);
        }
        else
        {
            foreach (var kv in block.Items)
            {
                sb.Append("  ");
                if (!kv.IsEnabled) sb.Append('~');
                sb.Append(kv.Key).Append(": ").AppendLine(kv.Value);
            }
        }

        sb.AppendLine("}");
    }

    private static bool ShouldWriteListBlock(BruBlock block) =>
        string.Equals(block.Name, "vars:secret", StringComparison.Ordinal) &&
        block.Items.All(kv => string.IsNullOrEmpty(kv.Value));

    private static void WriteListBlock(StringBuilder sb, BruBlock block)
    {
        sb.Append(block.Name).AppendLine(" [");

        for (var i = 0; i < block.Items.Count; i++)
        {
            var kv = block.Items[i];
            sb.Append("  ");
            if (!kv.IsEnabled) sb.Append('~');
            sb.Append(kv.Key);
            if (i < block.Items.Count - 1)
                sb.Append(',');
            sb.AppendLine();
        }

        sb.AppendLine("]");
    }
}
