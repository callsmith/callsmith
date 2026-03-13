using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace Callsmith.Desktop.ViewModels;

/// <summary>Returns true when a string is non-null and non-empty.</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public static readonly StringNotEmptyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Returns true when the value is not null.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public static readonly NotNullConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Returns true when the value is null.</summary>
public sealed class NullConverter : IValueConverter
{
    public static readonly NullConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

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

/// <summary>Converts HTTP method names (GET, POST, etc.) to accent brushes.</summary>
public sealed class HttpMethodToBrushConverter : IValueConverter
{
    public static readonly HttpMethodToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var method = value as string;
        var hex = method?.ToUpperInvariant() switch
        {
            "GET" => "#4ec9b0",
            "POST" => "#dda756",
            "PUT" => "#4fc1ff",
            "PATCH" => "#b8d7a3",
            "DELETE" => "#f48771",
            "HEAD" => "#c586c0",
            "OPTIONS" => "#9a9a9a",
            _ => "#d4d4d4",
        };

        return new SolidColorBrush(Color.Parse(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
