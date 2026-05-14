using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

public sealed class CommandPaletteSearchService : ICommandPaletteSearchService
{
    public IReadOnlyList<CommandPaletteSearchEntry> FlattenRequests(IReadOnlyList<CommandPaletteSearchNode> roots)
    {
        var results = new List<CommandPaletteSearchEntry>();
        foreach (var root in roots)
            WalkNode(root, string.Empty, results);
        return results;
    }

    public IReadOnlyList<CommandPaletteSearchEntry> Filter(
        IReadOnlyList<CommandPaletteSearchEntry> entries,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return entries;

        return entries.Where(e => FuzzyMatch(e.Request, query)).ToList();
    }

    private static void WalkNode(
        CommandPaletteSearchNode node,
        string pathPrefix,
        List<CommandPaletteSearchEntry> results)
    {
        if (!node.IsFolder && node.Request is { } request)
        {
            var displayPath = string.IsNullOrEmpty(pathPrefix)
                ? request.Name
                : $"{pathPrefix} / {request.Name}";

            results.Add(new CommandPaletteSearchEntry(
                request,
                displayPath,
                request.Method.Method));
            return;
        }

        var nextPrefix = node.IsRoot
            ? string.Empty
            : string.IsNullOrEmpty(pathPrefix) ? node.Name : $"{pathPrefix} / {node.Name}";

        foreach (var child in node.Children)
            WalkNode(child, nextPrefix, results);
    }

    private static bool FuzzyMatch(CollectionRequest request, string query)
    {
        var normQuery = Normalize(query);
        var normName = Normalize(request.Name);
        var normUrl = Normalize(request.Url);
        return 
            normName.Contains(normQuery, StringComparison.OrdinalIgnoreCase) || 
            normUrl.Contains(normQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) =>
        value
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
}
