using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Provides command-palette request flattening and fuzzy filtering.
/// </summary>
public interface ICommandPaletteSearchService
{
    IReadOnlyList<CommandPaletteSearchEntry> FlattenRequests(IReadOnlyList<CommandPaletteSearchNode> roots);

    IReadOnlyList<CommandPaletteSearchEntry> Filter(
        IReadOnlyList<CommandPaletteSearchEntry> entries,
        string query);
}
