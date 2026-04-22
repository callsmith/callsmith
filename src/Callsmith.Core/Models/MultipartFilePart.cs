namespace Callsmith.Core.Models;

/// <summary>
/// A multipart/form-data file part.
/// </summary>
public sealed class MultipartFilePart
{
    /// <summary>The multipart field name.</summary>
    public required string Key { get; init; }

    /// <summary>Raw file bytes to send for this part.</summary>
    public required byte[] FileBytes { get; init; }

    /// <summary>Original file name (for Content-Disposition metadata).</summary>
    public string? FileName { get; init; }

    /// <summary>Original local path selected by the user (display-only metadata).</summary>
    public string? FilePath { get; init; }

    /// <summary>Whether the part is enabled in the editor.</summary>
    public bool IsEnabled { get; init; } = true;
}
