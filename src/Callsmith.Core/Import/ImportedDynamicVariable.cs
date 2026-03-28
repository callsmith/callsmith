using Callsmith.Core.Models;

namespace Callsmith.Core.Import;

/// <summary>
/// A variable imported from an external format that has a dynamic (non-static) value.
/// Either a mock-data generator or a response-body extractor — never a composite.
/// </summary>
public sealed class ImportedDynamicVariable
{
    /// <summary>Variable name (key).</summary>
    public required string Name { get; init; }

    // ── Mock-data properties (when IsMockData is true) ─────────────────────

    /// <summary>Mock data category e.g. "Internet".</summary>
    public string? MockDataCategory { get; init; }

    /// <summary>Mock data field e.g. "Email".</summary>
    public string? MockDataField { get; init; }

    // ── Response-body properties (when IsResponseBody is true) ──────────────

    /// <summary>Collection-relative request path to execute.</summary>
    public string? ResponseRequestName { get; init; }

    /// <summary>Extractor expression to apply to the response body.</summary>
    public string? ResponsePath { get; init; }

    /// <summary>
    /// Matcher that interprets <see cref="ResponsePath"/>.
    /// Defaults to JSONPath for backward compatibility with imported collections.
    /// </summary>
    public ResponseValueMatcher ResponseMatcher { get; init; } = ResponseValueMatcher.JsonPath;

    /// <summary>Caching frequency for the response-body request.</summary>
    public DynamicFrequency ResponseFrequency { get; init; }

    /// <summary>Cache lifetime in seconds when <see cref="ResponseFrequency"/> is IfExpired.</summary>
    public int? ResponseExpiresAfterSeconds { get; init; }

    /// <summary>True when this is a mock-data generator variable.</summary>
    public bool IsMockData => MockDataCategory is not null;

    /// <summary>True when this is a response-body extractor variable.</summary>
    public bool IsResponseBody => ResponseRequestName is not null;
}
