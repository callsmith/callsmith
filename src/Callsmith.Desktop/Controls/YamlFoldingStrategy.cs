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
        Stack<(int Indent, int StartOffset, int StartLine, string Label, int ChildCount, bool IsScalarBlock)> blocks = [];

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
                TryAddFolding(document, foldings, block.StartOffset, block.StartLine, BuildFoldingName(block.Label, block.ChildCount), lastNonBlankLineEndOffset);
            }

            IncrementParentChildCount(blocks, indent);

            if (!IsBlockOpener(trimmed))
            {
                lastNonBlankLineEndOffset = line.EndOffset;
                continue;
            }

            var startOffset = line.Offset + indent;
            blocks.Push((indent, startOffset, lineNumber, BuildFoldingLabel(trimmed), CountInlineChildren(trimmed), IsScalarBlock(trimmed)));
            lastNonBlankLineEndOffset = line.EndOffset;
        }

        while (blocks.Count > 0)
        {
            var block = blocks.Pop();
            TryAddFolding(document, foldings, block.StartOffset, block.StartLine, BuildFoldingName(block.Label, block.ChildCount), lastNonBlankLineEndOffset);
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

    private static string BuildFoldingLabel(string trimmed)
    {
        if (trimmed.StartsWith("-", StringComparison.Ordinal))
            return "-";

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex <= 0)
            return string.Empty;

        var key = trimmed[..colonIndex].Trim();
        return key.Length == 0 ? string.Empty : $"{key}:";
    }

    private static string BuildFoldingName(string label, int childCount) =>
        string.IsNullOrEmpty(label)
            ? $"← {childCount} →"
            : $"{label} ← {childCount} →";

    private static bool IsScalarBlock(string trimmed)
    {
        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            var item = trimmed[2..].TrimStart();
            return item.StartsWith('|') || item.StartsWith('>');
        }

        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex < 0)
            return false;

        var suffix = trimmed[(colonIndex + 1)..].TrimStart();
        return suffix.StartsWith('|') || suffix.StartsWith('>');
    }

    private static int CountInlineChildren(string trimmed)
    {
        if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            return 0;

        var item = trimmed[2..].TrimStart();
        if (item.Length == 0 || item.StartsWith('|') || item.StartsWith('>'))
            return 0;

        return item.Contains(':') ? 1 : 0;
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

    private static void IncrementParentChildCount(
        Stack<(int Indent, int StartOffset, int StartLine, string Label, int ChildCount, bool IsScalarBlock)> blocks,
        int indent)
    {
        if (blocks.Count == 0)
            return;

        var parent = blocks.Peek();
        if (parent.IsScalarBlock || indent <= parent.Indent)
            return;

        blocks.Pop();
        blocks.Push(parent with { ChildCount = parent.ChildCount + 1 });
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