using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

public sealed class EnvironmentVariableSuggestionService : IEnvironmentVariableSuggestionService
{
    private const string SecretMask = "*****";

    public IReadOnlyList<EnvironmentVariableSuggestion> Build(params IEnumerable<EnvironmentVariable>?[] layers)
    {
        var merged = new Dictionary<string, EnvironmentVariable>(StringComparer.Ordinal);

        foreach (var layer in layers)
        {
            if (layer is null)
                continue;

            foreach (var variable in layer)
            {
                if (string.IsNullOrWhiteSpace(variable.Name))
                    continue;

                merged[variable.Name.Trim()] = variable;
            }
        }

        return merged.Values
            .OrderBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(v => new EnvironmentVariableSuggestion(
                v.Name.Trim(),
                v.IsSecret ? SecretMask : v.Value))
            .ToList();
    }
}
