using Avalonia.Data.Converters;
using Avalonia.Media;
using Callsmith.Desktop.ViewModels;
using System.Globalization;

namespace Callsmith.Desktop.Converters;

/// <summary>Converts HTTP method names (GET, POST, etc.) to accent brushes.</summary>
public sealed class HttpMethodToBrushConverter : IValueConverter
{
    public static readonly HttpMethodToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Color.Parse(HttpMethodColors.Hex(value as string)));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
