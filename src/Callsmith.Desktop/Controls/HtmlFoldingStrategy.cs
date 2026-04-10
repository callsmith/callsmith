using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// Creates folding sections for HTML element blocks and multi-line comments.
/// </summary>
internal sealed class HtmlFoldingStrategy
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

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

        var text = document.Text;
        List<NewFolding> foldings = [];
        Stack<(string Name, int Offset, int Line)> openTags = [];

        for (var offset = 0; offset < text.Length; offset++)
        {
            if (text[offset] != '<')
                continue;

            if (StartsWith(text, offset, "<!--"))
            {
                var commentEnd = text.IndexOf("-->", offset + 4, StringComparison.Ordinal);
                if (commentEnd < 0)
                    break;

                TryAddFolding(document, foldings, offset, commentEnd + 3, "<!--...-->");
                offset = commentEnd + 2;
                continue;
            }

            var tagEnd = FindTagEnd(text, offset + 1);
            if (tagEnd < 0)
                break;

            if (offset + 1 < text.Length && text[offset + 1] == '!')
            {
                offset = tagEnd;
                continue;
            }

            if (offset + 1 < text.Length && text[offset + 1] == '?')
            {
                offset = tagEnd;
                continue;
            }

            if (offset + 1 < text.Length && text[offset + 1] == '/')
            {
                var closingName = ParseTagName(text, offset + 2);
                if (string.IsNullOrEmpty(closingName))
                {
                    offset = tagEnd;
                    continue;
                }

                if (!TryPopMatchingTag(openTags, closingName, out var openTag))
                {
                    offset = tagEnd;
                    continue;
                }

                var closeEndOffset = tagEnd + 1;
                var endLine = document.GetLineByOffset(closeEndOffset - 1).LineNumber;
                if (openTag.Line < endLine)
                {
                    foldings.Add(new NewFolding(openTag.Offset, closeEndOffset)
                    {
                        Name = $"<{closingName}>...</{closingName}>",
                    });
                }

                offset = tagEnd;
                continue;
            }

            var openingName = ParseTagName(text, offset + 1);
            if (string.IsNullOrEmpty(openingName))
            {
                offset = tagEnd;
                continue;
            }

            if (IsSelfClosingTag(text, tagEnd) || VoidElements.Contains(openingName))
            {
                offset = tagEnd;
                continue;
            }

            var line = document.GetLineByOffset(offset).LineNumber;
            openTags.Push((openingName, offset, line));
            offset = tagEnd;
        }

        foldings.Sort((left, right) => left.StartOffset.CompareTo(right.StartOffset));
        return foldings;
    }

    private static bool TryPopMatchingTag(
        Stack<(string Name, int Offset, int Line)> openTags,
        string closingName,
        out (string Name, int Offset, int Line) openTag)
    {
        if (openTags.Count == 0)
        {
            openTag = default;
            return false;
        }

        while (openTags.Count > 0)
        {
            var candidate = openTags.Pop();
            if (string.Equals(candidate.Name, closingName, StringComparison.OrdinalIgnoreCase))
            {
                openTag = candidate;
                return true;
            }
        }

        openTag = default;
        return false;
    }

    private static bool IsSelfClosingTag(string text, int tagEnd)
    {
        for (var i = tagEnd - 1; i >= 0; i--)
        {
            var current = text[i];
            if (char.IsWhiteSpace(current))
                continue;

            return current == '/';
        }

        return false;
    }

    private static string ParseTagName(string text, int start)
    {
        var i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        var nameStart = i;
        while (i < text.Length && IsNameCharacter(text[i]))
            i++;

        if (i == nameStart)
            return string.Empty;

        return text[nameStart..i];
    }

    private static bool IsNameCharacter(char c) =>
        char.IsLetterOrDigit(c) || c is ':' or '-' or '_' or '.';

    private static int FindTagEnd(string text, int start)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = start; i < text.Length; i++)
        {
            var current = text[i];

            if (current == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (current == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (current == '>' && !inSingleQuote && !inDoubleQuote)
                return i;
        }

        return -1;
    }

    private static bool StartsWith(string text, int offset, string token)
    {
        if (offset + token.Length > text.Length)
            return false;

        for (var i = 0; i < token.Length; i++)
        {
            if (text[offset + i] != token[i])
                return false;
        }

        return true;
    }

    private static void TryAddFolding(
        TextDocument document,
        ICollection<NewFolding> foldings,
        int startOffset,
        int endOffset,
        string name)
    {
        var startLine = document.GetLineByOffset(startOffset).LineNumber;
        var endLine = document.GetLineByOffset(endOffset - 1).LineNumber;
        if (startLine >= endLine)
            return;

        foldings.Add(new NewFolding(startOffset, endOffset) { Name = name });
    }
}