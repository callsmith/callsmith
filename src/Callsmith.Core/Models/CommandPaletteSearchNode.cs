namespace Callsmith.Core.Models;

/// <summary>
/// Tree node input used by command palette search services.
/// </summary>
public sealed record CommandPaletteSearchNode
{
    public required string Name { get; init; }
    public required bool IsFolder { get; init; }
    public required bool IsRoot { get; init; }
    public CollectionRequest? Request { get; init; }
    public IReadOnlyList<CommandPaletteSearchNode> Children { get; init; } = [];
}
