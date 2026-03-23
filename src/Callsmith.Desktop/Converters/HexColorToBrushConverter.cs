using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Callsmith.Desktop.Converters;

/// <summary>Converts a hex color string (e.g. "#1a5c33") to an Avalonia <see cref="IBrush"/>.</summary>
public sealed class HexColorToBrushConverter : IValueConverter
{
    public static readonly HexColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string { Length: > 0 } hex)
            try { return new SolidColorBrush(Color.Parse(hex)); }
            catch { return null; }
        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
