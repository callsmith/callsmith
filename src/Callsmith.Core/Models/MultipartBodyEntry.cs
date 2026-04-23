namespace Callsmith.Core.Models;

/// <summary>
/// A single entry in a multipart/form-data body, preserving the user-defined insertion order.
/// Each entry is either a text field or a file field.
/// </summary>
public sealed class MultipartBodyEntry
{
    /// <summary>The multipart field name.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary><see langword="true"/> if this is a file part; <see langword="false"/> for a text field.</summary>
    public bool IsFile { get; init; }

    /// <summary>Text value. Populated only when <see cref="IsFile"/> is <see langword="false"/>.</summary>
    public string? TextValue { get; init; }

    /// <summary>Original file name (Content-Disposition metadata). Populated only when <see cref="IsFile"/> is <see langword="true"/>.</summary>
    public string? FileName { get; init; }

    /// <summary>Local path on disk selected by the user (display metadata). Populated only when <see cref="IsFile"/> is <see langword="true"/>.</summary>
    public string? FilePath { get; init; }

    /// <summary>Whether this entry is enabled in the editor.</summary>
    public bool IsEnabled { get; init; } = true;
}
