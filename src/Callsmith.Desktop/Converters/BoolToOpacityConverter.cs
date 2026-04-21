using System.Globalization;
using Avalonia.Data.Converters;

namespace Callsmith.Desktop.Converters;

/// <summary>
/// Maps a bool to an opacity value. True → 1.0 (fully opaque); false → 0.0 (invisible).
/// Use this to hide elements while keeping them in the layout (space reserved), analogous
/// to CSS <c>visibility: hidden</c> rather than <c>display: none</c>.
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
