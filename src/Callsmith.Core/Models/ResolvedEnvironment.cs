using Callsmith.Core.MockData;

namespace Callsmith.Core.Models;

/// <summary>
/// The result of resolving all environment variables for a given request send.
/// <list type="bullet">
///   <item><see cref="Variables"/> \u2014 pre-computed string values (static and response-body types).</item>
///   <item><see cref="MockGenerators"/> \u2014 mock-data vars that must be re-evaluated per token
///     reference to ensure each usage of <c>{{var}}</c> within the same request yields a
///     different generated value.</item>
/// </list>
/// </summary>
public sealed class ResolvedEnvironment
{
    /// <summary>Pre-computed name \u2192 value map (static and already-fetched response-body vars).</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Mock-data generators: name → catalog entry.
    /// Each token reference to a key in this map will call <c>MockDataCatalog.Generate</c> freshly
    /// so that two occurrences of <c>{{mock-email}}</c> in the same request template yield
    /// two different generated values.
    /// </summary>
    public IReadOnlyDictionary<string, MockDataEntry> MockGenerators { get; init; }
        = new Dictionary<string, MockDataEntry>();

    /// <summary>
    /// Names of response-body variables that were attempted but failed to produce a value
    /// (API unreachable, path/regex did not match, or evaluation threw an exception).
    /// Variables absent because they are not yet configured are not included.
    /// </summary>
    public IReadOnlySet<string> FailedVariables { get; init; }
        = new HashSet<string>();

    public static readonly ResolvedEnvironment Empty = new();
}
