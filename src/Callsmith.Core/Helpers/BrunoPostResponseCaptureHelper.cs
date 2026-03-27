using Callsmith.Core.Models;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Applies Bruno <c>vars:post-response</c> captures to an environment after a request completes.
/// <para>
/// Each capture expression (e.g. <c>res.body.token</c>) is evaluated against the JSON response body
/// using JSONPath, and the extracted value is written to the named variable in the environment as a
/// plain <c>static</c> value.  Variables that do not yet exist in the environment are created.
/// Captures whose expressions cannot be evaluated (non-body paths, missing JSON path, empty body)
/// are silently skipped.
/// </para>
/// </summary>
public static class BrunoPostResponseCaptureHelper
{
    private const string BodyPrefix = "res.body.";

    /// <summary>
    /// Evaluates each entry in <paramref name="captures"/> against <paramref name="responseBody"/>
    /// and returns an updated <see cref="EnvironmentModel"/> whose variables reflect the extracted
    /// values.  Returns the same <paramref name="environment"/> instance unchanged when no captures
    /// produce a result.
    /// </summary>
    /// <param name="captures">
    /// The <see cref="CollectionRequest.BrunoPostResponseCaptures"/> list from the executed request.
    /// </param>
    /// <param name="responseBody">Raw JSON response body.</param>
    /// <param name="environment">The currently active environment to update.</param>
    public static EnvironmentModel Apply(
        IReadOnlyList<KeyValuePair<string, string>> captures,
        string responseBody,
        EnvironmentModel environment)
    {
        if (captures.Count == 0 || string.IsNullOrEmpty(responseBody))
            return environment;

        // Index existing variables by name for O(1) lookup.
        var variables = environment.Variables
            .ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);

        var changed = false;

        foreach (var (varName, brunoExpr) in captures)
        {
            if (string.IsNullOrWhiteSpace(varName)) continue;

            // Only body captures are supported for now (res.body.*).
            // Non-body captures such as res.headers.X are silently skipped.
            if (!brunoExpr.StartsWith(BodyPrefix, StringComparison.Ordinal)) continue;

            var jsonPath = "$." + brunoExpr[BodyPrefix.Length..];
            var extracted = JsonPathHelper.Extract(responseBody, jsonPath);
            if (extracted is null) continue;

            if (variables.TryGetValue(varName, out var existing))
                variables[varName] = existing with { Value = extracted };
            else
                variables[varName] = new EnvironmentVariable
                {
                    Name = varName,
                    Value = extracted,
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                };

            changed = true;
        }

        if (!changed) return environment;

        // Rebuild the variable list: preserve original order for existing variables,
        // then append any newly created ones at the end.
        var existingNames = environment.Variables
            .Select(v => v.Name)
            .ToHashSet(StringComparer.Ordinal);

        var updatedVars = environment.Variables
            .Select(v => variables.TryGetValue(v.Name, out var updated) ? updated : v)
            .Concat(variables.Values.Where(v => !existingNames.Contains(v.Name)))
            .ToList();

        return environment with { Variables = updatedVars };
    }
}
