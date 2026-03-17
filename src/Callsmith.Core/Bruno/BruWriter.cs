using System.Text;

namespace Callsmith.Core.Bruno;

/// <summary>
/// Serialises a sequence of <see cref="BruBlock"/> entries back to <c>.bru</c> text.
/// Blocks are separated by a single blank line; the file ends with a trailing newline.
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
            if (!first) sb.AppendLine();
            first = false;
            WriteBlock(sb, block);
        }

        return sb.ToString().TrimEnd('\r', '\n') + "\n";
    }

    private static void WriteBlock(StringBuilder sb, BruBlock block)
    {
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
}
