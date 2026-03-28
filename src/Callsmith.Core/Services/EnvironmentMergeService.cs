using Callsmith.Core.Abstractions;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

/// <summary>
/// Implements the canonical three-layer environment variable merge used at request send
/// time. Both <c>RequestTabViewModel</c> and <c>EnvironmentEditorViewModel</c> delegate
/// to this service so that any change to the merge algorithm — precedence rules, dynamic
/// resolution order, force-override logic — is automatically reflected in both the live
/// send and the editor preview.
/// </summary>
public sealed class EnvironmentMergeService : IEnvironmentMergeService
{
    private readonly IDynamicVariableEvaluator? _evaluator;

    public EnvironmentMergeService(IDynamicVariableEvaluator? evaluator = null)
    {
        _evaluator = evaluator;
    }

    /// <inheritdoc/>
    public Dictionary<string, string> BuildStaticMerge(EnvironmentModel globalEnv, EnvironmentModel? activeEnv)
    {
        var merged = globalEnv.Variables.ToDictionary(v => v.Name, v => v.Value);
        if (activeEnv is not null)
            foreach (var v in activeEnv.Variables)
                merged[v.Name] = v.Value;

        // Force-override global vars take final priority — re-apply them after the active env.
        foreach (var v in globalEnv.Variables.Where(v => v.IsForceGlobalOverride && !string.IsNullOrWhiteSpace(v.Name)))
            merged[v.Name] = v.Value;

        return merged;
    }

    /// <inheritdoc/>
    public async Task<ResolvedEnvironment> MergeAsync(
        string collectionFolderPath,
        EnvironmentModel globalEnv,
        EnvironmentModel? activeEnv,
        CancellationToken ct = default)
    {
        var merged = BuildStaticMerge(globalEnv, activeEnv);

        if (_evaluator is null)
            return new ResolvedEnvironment { Variables = merged };

        var globalVars = globalEnv.Variables;
        var globalHasDynamic = globalVars.Any(v =>
            v.VariableType is EnvironmentVariable.VariableTypes.Dynamic
                or EnvironmentVariable.VariableTypes.ResponseBody
                or EnvironmentVariable.VariableTypes.MockData);

        var activeVars = activeEnv?.Variables ?? (IReadOnlyList<EnvironmentVariable>)[];
        var activeHasDynamic = activeVars.Any(v =>
            v.VariableType is EnvironmentVariable.VariableTypes.Dynamic
                or EnvironmentVariable.VariableTypes.ResponseBody
                or EnvironmentVariable.VariableTypes.MockData);

        if (!globalHasDynamic && !activeHasDynamic)
            return new ResolvedEnvironment { Variables = merged };

        var allMockGenerators = new Dictionary<string, MockDataEntry>();
        // Capture resolved values for force-override global dynamic vars so they can be
        // re-applied at the end after the active env's dynamic resolution overwrites them.
        var forceOverrideDynamicValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var forceOverrideMockGenerators = new Dictionary<string, MockDataEntry>();
        try
        {
            // 1. Resolve global dynamic vars first, passing the full merged static dict so that
            //    global requests can use active-env vars (e.g. baseUrl) when they need them.
            if (globalHasDynamic)
            {
                // Scope the global var cache per active environment — a global token request
                // uses the active env's credentials/baseUrl, so each env gets its own token.
                var globalCacheNamespace = activeEnv is not null
                    ? $"{globalEnv.EnvironmentId:N}[env:{activeEnv.EnvironmentId:N}]"
                    : globalEnv.EnvironmentId.ToString("N");

                var globalResolved = await _evaluator.ResolveAsync(
                    collectionFolderPath,
                    globalCacheNamespace,
                    globalVars,
                    merged,
                    ct).ConfigureAwait(false);

                foreach (var kv in globalResolved.Variables)
                    merged[kv.Key] = kv.Value;
                foreach (var kv in globalResolved.MockGenerators)
                    allMockGenerators[kv.Key] = kv.Value;

                // Save the resolved values of any force-override dynamic global vars so they
                // can be re-applied at the end after the active env potentially overwrites them.
                foreach (var v in globalVars.Where(v => v.IsForceGlobalOverride && !string.IsNullOrWhiteSpace(v.Name)))
                {
                    if (globalResolved.Variables.TryGetValue(v.Name, out var resolvedVal))
                        forceOverrideDynamicValues[v.Name] = resolvedVal;
                    if (globalResolved.MockGenerators.TryGetValue(v.Name, out var mockGen))
                        forceOverrideMockGenerators[v.Name] = mockGen;
                }

                // Active-env static vars must still win over global resolved values.
                if (activeEnv is not null)
                    foreach (var v in activeEnv.Variables
                        .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.Static
                                 && !string.IsNullOrWhiteSpace(v.Name)))
                        merged[v.Name] = v.Value;
            }

            // 2. Resolve active-env dynamic vars with global values now available in merged.
            if (activeHasDynamic && activeEnv is not null)
            {
                var activeResolved = await _evaluator.ResolveAsync(
                    collectionFolderPath,
                    activeEnv.EnvironmentId.ToString("N"),
                    activeEnv.Variables,
                    merged,
                    ct).ConfigureAwait(false);

                foreach (var kv in activeResolved.Variables)
                    merged[kv.Key] = kv.Value;
                foreach (var kv in activeResolved.MockGenerators)
                    allMockGenerators[kv.Key] = kv.Value;
            }

            // 3. Re-apply force-override global vars so they win over active-env vars.
            //    Static force-override globals are re-applied directly; dynamic ones use
            //    the resolved values captured in step 1.
            foreach (var v in globalVars.Where(v => v.IsForceGlobalOverride && !string.IsNullOrWhiteSpace(v.Name)))
            {
                if (v.VariableType == EnvironmentVariable.VariableTypes.Static)
                    merged[v.Name] = v.Value;
            }
            foreach (var kv in forceOverrideDynamicValues)
                merged[kv.Key] = kv.Value;
            foreach (var kv in forceOverrideMockGenerators)
                allMockGenerators[kv.Key] = kv.Value;

            return new ResolvedEnvironment { Variables = merged, MockGenerators = allMockGenerators };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // If evaluation fails, continue with static values so the request still sends.
            return new ResolvedEnvironment { Variables = merged };
        }
    }
}
