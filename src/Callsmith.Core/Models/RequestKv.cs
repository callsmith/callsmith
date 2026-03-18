namespace Callsmith.Core.Models;

/// <summary>
/// An immutable key/value pair with an optional enabled flag, used to represent
/// request headers and query parameters that may be individually disabled without
/// being removed from the saved request.
/// </summary>
public sealed record RequestKv(string Key, string Value, bool IsEnabled = true);
