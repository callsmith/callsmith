namespace Callsmith.Core.Models;

/// <summary>
/// Distinct environment option sourced from history storage for UI filtering.
/// </summary>
public sealed record HistoryEnvironmentOption
{
    /// <summary>Environment display name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional stable environment identifier captured in history.
    /// Null when the entry predates id capture or no environment was selected.
    /// </summary>
    public Guid? Id { get; init; }

    /// <summary>
    /// Optional stored environment color (hex string, e.g. #4ec9b0).
    /// Null when no color was captured.
    /// </summary>
    public string? Color { get; init; }
}
