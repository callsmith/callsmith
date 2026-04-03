namespace Callsmith.Core.Models;

/// <summary>A name and/or value pair used to search through request or response headers.</summary>
/// <param name="Name">Header name to search for. Null means match any name.</param>
/// <param name="Value">Header value to match (case-insensitive contains). Null means match any value.</param>
public sealed record HeaderSearch(string? Name, string? Value);
