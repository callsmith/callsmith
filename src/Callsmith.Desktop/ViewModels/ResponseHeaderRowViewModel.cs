namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Read-only row model for displaying response headers in a two-column zebra table.
/// </summary>
public sealed class ResponseHeaderRowViewModel
{
    private const string EvenRowColor = "#252526";
    private const string OddRowColor = "#2d2d30";

    public string Name { get; }
    public string Value { get; }
    public string RowBackground { get; }

    public ResponseHeaderRowViewModel(string name, string value, int rowIndex)
    {
        Name = name;
        Value = value;
        RowBackground = rowIndex % 2 == 0 ? EvenRowColor : OddRowColor;
    }
}