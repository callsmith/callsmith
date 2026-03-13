using Avalonia.Data.Converters;
using System.Globalization;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Returns a folder or file icon character based on a boolean value.
/// Used in the collections tree to distinguish folder nodes from request nodes.
/// </summary>
public sealed class BoolToIconConverter : IValueConverter
{
    /// <summary>Shared instance for use in AXAML bindings.</summary>
    public static readonly BoolToIconConverter FolderOrRequest = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "📁" : "→";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
