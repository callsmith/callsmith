namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Represents one environment option in the history filter dropdown.
/// </summary>
public sealed class HistoryEnvironmentOptionViewModel
{
    public required string Name { get; init; }

    /// <summary>
    /// Stable environment identifier for rename-safe filtering. Null for legacy options.
    /// </summary>
    public Guid? Id { get; init; }

    /// <summary>
    /// Hex color string (e.g. #4ec9b0) used to render the swatch, or null for no swatch.
    /// </summary>
    public string? Color { get; init; }
}