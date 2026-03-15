namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Maps HTTP method names to their accent color hex strings.
/// Single source of truth for all method-color mappings in the application.
/// </summary>
internal static class HttpMethodColors
{
    /// <summary>Returns the hex color string for a given HTTP method name.</summary>
    public static string Hex(string? method) => method?.ToUpperInvariant() switch
    {
        "GET"     => "#4ec9b0",
        "POST"    => "#dda756",
        "PUT"     => "#4fc1ff",
        "PATCH"   => "#b8d7a3",
        "DELETE"  => "#f48771",
        "HEAD"    => "#c586c0",
        "OPTIONS" => "#9a9a9a",
        _         => "#d4d4d4",
    };
}
