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
        Stack<(char Delimiter, int Offset, int Line)> delimiters = [];

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
                continue;
            }

            if (current is '{' or '[')
            {
                var startLine = document.GetLineByOffset(offset).LineNumber;
                delimiters.Push((current, offset, startLine));
                continue;
            }

            if (current is not '}' and not ']')
                continue;

            if (delimiters.Count == 0)
                continue;

            var open = delimiters.Peek();
            if (!IsMatch(open.Delimiter, current))
                continue;

            delimiters.Pop();

            var endLine = document.GetLineByOffset(offset).LineNumber;
            if (open.Line >= endLine)
                continue;

            foldings.Add(new NewFolding(open.Offset, offset + 1)
            {
                Name = open.Delimiter == '{' ? "{...}" : "[...]",
            });
        }

        foldings.Sort((left, right) => left.StartOffset.CompareTo(right.StartOffset));
        return foldings;
    }

    private static bool IsMatch(char open, char close) =>
        (open == '{' && close == '}') ||
        (open == '[' && close == ']');
}