using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// Creates folding sections for YAML blocks using indentation rules.
/// </summary>
internal sealed class YamlFoldingStrategy
{
    public void UpdateFoldings(FoldingManager manager, TextDocument? document)
    {
        ArgumentNullException.ThrowIfNull(manager);

        if (document is null)
        {
            manager.UpdateFoldings([], -1);
            return;
        }

        manager.UpdateFoldings(CreateNewFoldings(document), -1);
    }

    internal IReadOnlyList<NewFolding> CreateNewFoldings(TextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<NewFolding> foldings = [];
        Stack<(int Indent, int StartOffset, int StartLine, string Name)> blocks = [];

        var lastNonBlankLineEndOffset = 0;

        for (var lineNumber = 1; lineNumber <= document.LineCount; lineNumber++)
        {
            var line = document.GetLineByNumber(lineNumber);
            var lineText = document.GetText(line.Offset, line.Length);
            var trimmed = lineText.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var indent = CountIndent(lineText);

            while (blocks.Count > 0 && indent <= blocks.Peek().Indent)
            {
                var block = blocks.Pop();
                TryAddFolding(document, foldings, block.StartOffset, block.StartLine, block.Name, lastNonBlankLineEndOffset);
            }

            if (!IsBlockOpener(trimmed))
            {
                lastNonBlankLineEndOffset = line.EndOffset;
                continue;
            }

            var startOffset = line.Offset + indent;
            blocks.Push((indent, startOffset, lineNumber, BuildFoldingName(trimmed)));
            lastNonBlankLineEndOffset = line.EndOffset;
        }

        while (blocks.Count > 0)
        {
            var block = blocks.Pop();
            TryAddFolding(document, foldings, block.StartOffset, block.StartLine, block.Name, lastNonBlankLineEndOffset);
        }

        foldings.Sort((left, right) => left.StartOffset.CompareTo(right.StartOffset));
        return foldings;
    }

    private static bool IsBlockOpener(string trimmed)
    {
        if (trimmed == "-")
            return true;

        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            var item = trimmed[2..].TrimStart();
            if (item.Length == 0)
                return true;

            if (item.StartsWith('|') || item.StartsWith('>'))
                return true;

            if (item.EndsWith(':'))
                return true;

            if (item.Contains(':'))
                return true;

            return false;
        }

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex >= 0)
        {
            if (colonIndex == trimmed.Length - 1)
                return true;

            var suffix = trimmed[(colonIndex + 1)..].TrimStart();
            if (suffix.StartsWith('|') || suffix.StartsWith('>'))
                return true;
        }

        return false;
    }

    private static string BuildFoldingName(string trimmed)
    {
        if (trimmed.StartsWith("-", StringComparison.Ordinal))
            return "- ...";

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex <= 0)
            return "...";

        var key = trimmed[..colonIndex].Trim();
        return key.Length == 0 ? "..." : $"{key}: ...";
    }

    private static int CountIndent(string line)
    {
        var indent = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                indent++;
                continue;
            }

            if (c == '\t')
            {
                indent += 2;
                continue;
            }

            break;
        }

        return indent;
    }

    private static void TryAddFolding(
        TextDocument document,
        ICollection<NewFolding> foldings,
        int startOffset,
        int startLine,
        string name,
        int endOffset)
    {
        if (endOffset <= startOffset)
            return;

        var endLine = document.GetLineByOffset(endOffset - 1).LineNumber;
        if (startLine >= endLine)
            return;

        foldings.Add(new NewFolding(startOffset, endOffset) { Name = name });
    }
}