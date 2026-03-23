using System.Globalization;
using Avalonia.Data.Converters;

namespace Callsmith.Desktop.Converters;

/// <summary>
/// Converts a relative folder path (used in Save As) to a display label.
/// An empty string (collection root) is shown as "/ (root)"; all other paths are shown as-is.
/// </summary>
public sealed class FolderPathDisplayConverter : IValueConverter
{
    public static readonly FolderPathDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 ? s : "/ (root)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
