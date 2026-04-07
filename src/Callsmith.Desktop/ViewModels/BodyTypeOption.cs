namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Represents one item in the body-type <see cref="Avalonia.Controls.ComboBox"/>.
/// May be a selectable body type or a non-interactive visual separator.
/// </summary>
public sealed record BodyTypeOption
{
    /// <summary>The <see cref="Core.Models.CollectionRequest.BodyTypes"/> constant value, or empty for separators.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>User-visible label shown in the combo box.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// When <see langword="true"/> this item renders as a horizontal divider and
    /// cannot be selected.
    /// </summary>
    public bool IsSeparator { get; init; }

    /// <summary>Convenience factory for a visual separator item.</summary>
    public static BodyTypeOption Separator { get; } = new() { IsSeparator = true };
}
