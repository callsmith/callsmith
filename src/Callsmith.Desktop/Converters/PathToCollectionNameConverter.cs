using Avalonia.Data.Converters;
using System.Globalization;

namespace Callsmith.Desktop.Converters;

/// <summary>
/// Converts a full folder path to just the collection folder name (the last path segment).
/// Falls back to the full path if the name cannot be determined.
/// </summary>
public sealed class PathToCollectionNameConverter : IValueConverter
{
    public static readonly PathToCollectionNameConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
            return value ?? string.Empty;

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
