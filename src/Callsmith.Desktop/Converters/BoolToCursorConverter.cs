using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;

namespace Callsmith.Desktop.Converters;

/// <summary>
/// Maps a boolean to an Avalonia <see cref="Cursor"/>.
/// True → the default arrow cursor; false → the Help cursor.
/// Used on wrapper elements that stay hit-testable while their inner control is disabled,
/// so the user sees a visual cue that hovering will reveal an explanatory tooltip.
/// </summary>
public sealed class BoolToCursorConverter : IValueConverter
{
    /// <summary>Shared instance for use in AXAML bindings.</summary>
    public static readonly BoolToCursorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? new Cursor(StandardCursorType.Arrow)
            : new Cursor(StandardCursorType.Help);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
