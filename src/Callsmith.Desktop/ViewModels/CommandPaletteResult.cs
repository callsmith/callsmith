using Callsmith.Core.Models;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// A single item shown in the command palette results list.
/// </summary>
public sealed record CommandPaletteResult(
    CollectionRequest Request,
    string DisplayPath,
    string MethodName);
