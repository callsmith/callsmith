using Avalonia.Data.Converters;
using System.Globalization;

namespace Callsmith.Desktop.Converters;

/// <summary>
/// Maps a bool to an opacity value. True → 1.0 (fully opaque); false → 0.35 (dimmed).
/// Used to make always-present buttons appear visually inactive when disabled.
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.35;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
