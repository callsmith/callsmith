using Avalonia.Data.Converters;
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
