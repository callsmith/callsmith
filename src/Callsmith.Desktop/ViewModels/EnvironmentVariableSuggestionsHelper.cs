using Callsmith.Core.Models;
using Callsmith.Desktop.Controls;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Shared helper for building <see cref="EnvVarSuggestion"/> lists from one or more layers of
/// <see cref="EnvironmentVariable"/> collections. Later layers override earlier ones when two
/// variables share the same name (global → active is the canonical order).
/// </summary>
internal static class EnvironmentVariableSuggestionsHelper
{
    private const string SecretMask = "\u2022\u2022\u2022\u2022\u2022"; // •••••

    /// <summary>
    /// Merges <paramref name="layers"/> of environment variables (lowest-priority first),
    /// deduplicates by name, and projects each entry to an <see cref="EnvVarSuggestion"/>.
    /// Secret variable values are replaced with a bullet mask.
    /// Results are sorted alphabetically by name.
    /// </summary>
    /// <param name="layers">
    /// Variable layers in ascending-priority order (e.g. global first, then active environment).
    /// <see langword="null"/> layers and entries with blank names are silently skipped.
    /// </param>
    public static IReadOnlyList<EnvVarSuggestion> Build(
        params IEnumerable<EnvironmentVariable>?[] layers)
    {
        var merged = new Dictionary<string, EnvironmentVariable>(StringComparer.Ordinal);

        foreach (var layer in layers)
        {
            if (layer is null) continue;
            foreach (var v in layer)
            {
                if (string.IsNullOrWhiteSpace(v.Name)) continue;
                merged[v.Name.Trim()] = v;
            }
        }

        return merged.Values
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new EnvVarSuggestion(v.Name.Trim(), v.IsSecret ? SecretMask : v.Value))
            .ToList();
    }
}
