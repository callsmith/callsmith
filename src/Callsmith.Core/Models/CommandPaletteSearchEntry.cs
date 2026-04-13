namespace Callsmith.Core.Models;

/// <summary>
/// Flattened request entry used by command palette filtering.
/// </summary>
public sealed record CommandPaletteSearchEntry(
    CollectionRequest Request,
    string DisplayPath,
    string MethodName);
