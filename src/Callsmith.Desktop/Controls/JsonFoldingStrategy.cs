using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// Creates folding sections for JSON objects and arrays.
/// </summary>
internal sealed class JsonFoldingStrategy
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

        // ChildCount: colons seen for objects, commas seen for arrays.
        // HasContent: true once any non-whitespace token is seen inside an array,
        // used to distinguish empty [] from a single-element array (no comma).
        Stack<(char Delimiter, int Offset, int Line, int ChildCount, bool HasContent)> delimiters = [];

        var inString = false;
        var escaped = false;

        for (var offset = 0; offset < document.TextLength; offset++)
        {
            var current = document.GetCharAt(offset);

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                    inString = false;

                continue;
            }

            if (current == '"')
            {
                inString = true;
                MarkArrayHasContent();
                continue;
            }

            if (current is '{' or '[')
            {
                // Nested container counts as content for its parent array.
                MarkArrayHasContent();
                var startLine = document.GetLineByOffset(offset).LineNumber;
                delimiters.Push((current, offset, startLine, 0, false));
                continue;
            }

            // Count object properties by colons at the immediate object level.
            if (current == ':' && delimiters.Count > 0 && delimiters.Peek().Delimiter == '{')
            {
                var top = delimiters.Pop();
                delimiters.Push(top with { ChildCount = top.ChildCount + 1 });
                continue;
            }

            // Count array elements by commas at the immediate array level.
            if (current == ',' && delimiters.Count > 0 && delimiters.Peek().Delimiter == '[')
            {
                var top = delimiters.Pop();
                delimiters.Push(top with { ChildCount = top.ChildCount + 1 });
                continue;
            }

            if (current is not '}' and not ']')
            {
                // Any non-whitespace literal value (number, bool, null) is content in an array.
                if (!char.IsWhiteSpace(current))
                    MarkArrayHasContent();
                continue;
            }

            if (delimiters.Count == 0)
                continue;

            var open = delimiters.Peek();
            if (!IsMatch(open.Delimiter, current))
                continue;

            delimiters.Pop();

            var endLine = document.GetLineByOffset(offset).LineNumber;
            if (open.Line >= endLine)
                continue;

            // Objects: one colon per property. Arrays: commas + 1 when non-empty, else 0.
            var childCount = open.Delimiter == '{'
                ? open.ChildCount
                : (open.HasContent ? open.ChildCount + 1 : 0);

            var placeholder = open.Delimiter == '{'
                ? $"{{← {childCount} →}}"
                : $"[← {childCount} →]";

            foldings.Add(new NewFolding(open.Offset, offset + 1)
            {
                Name = placeholder,
            });
        }

        foldings.Sort((left, right) => left.StartOffset.CompareTo(right.StartOffset));
        return foldings;

        void MarkArrayHasContent()
        {
            if (delimiters.Count > 0 && delimiters.Peek().Delimiter == '[' && !delimiters.Peek().HasContent)
            {
                var top = delimiters.Pop();
                delimiters.Push(top with { HasContent = true });
            }
        }
    }

    private static bool IsMatch(char open, char close) =>
        (open == '{' && close == '}') ||
        (open == '[' && close == ']');
}