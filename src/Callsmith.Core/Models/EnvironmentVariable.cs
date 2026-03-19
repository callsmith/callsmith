using Callsmith.Core.MockData;

namespace Callsmith.Core.Models;

/// <summary>
/// A single variable within an environment.
/// </summary>
public sealed class EnvironmentVariable
{
    /// <summary>The variable name used in <c>{{variableName}}</c> substitution.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The variable value. For <see cref="VariableTypes.Static"/> this is the literal value.
    /// For dynamic types this holds the last cached result (may be empty until first evaluation).
    /// </summary>
    public required string Value { get; init; }

    /// <summary>The variable type — determines how the value is resolved at send time.</summary>
    public string VariableType { get; init; } = VariableTypes.Static;

    /// <summary>
    /// Whether this variable holds a secret value (token, password, API key, etc.).
    /// Secret variables are masked in the UI and never written to request history in plaintext.
    /// </summary>
    public bool IsSecret { get; init; }

    // ── Mock data type properties (VariableType == VariableTypes.MockData) ─────

    /// <summary>Mock data category e.g. "Internet". Only relevant for mock-data variables.</summary>
    public string? MockDataCategory { get; init; }

    /// <summary>Mock data field e.g. "Email". Only relevant for mock-data variables.</summary>
    public string? MockDataField { get; init; }

    // ── Response body type properties (VariableType == VariableTypes.ResponseBody) ─

    /// <summary>
    /// Collection-relative request path used to identify which request to execute.
    /// Only relevant for response-body variables.
    /// </summary>
    public string? ResponseRequestName { get; init; }

    /// <summary>
    /// JSONPath expression used to extract the desired value from the response body.
    /// Only relevant for response-body variables.
    /// </summary>
    public string? ResponsePath { get; init; }

    /// <summary>How often the linked request should be re-executed. Defaults to Always.</summary>
    public DynamicFrequency ResponseFrequency { get; init; }

    /// <summary>
    /// Cache lifetime in seconds when <see cref="ResponseFrequency"/> is
    /// <see cref="DynamicFrequency.IfExpired"/>. Defaults to 900 (15 minutes).
    /// </summary>
    public int? ResponseExpiresAfterSeconds { get; init; }

    // ── Backward-compat: old segment-based dynamic vars ───────────────────────

    /// <summary>
    /// Legacy: composite value segments from the old inline-pill model.
    /// Kept for reading data written by older versions. Migrated to typed properties on load.
    /// New code must not write this property.
    /// </summary>
    public IReadOnlyList<ValueSegment>? Segments { get; init; }

    /// <summary>
    /// Returns the <see cref="MockDataEntry"/> for this variable when it is a mock-data type,
    /// or <see langword="null"/> otherwise.
    /// </summary>
    public MockDataEntry? GetMockEntry()
    {
        if (VariableType != VariableTypes.MockData) return null;
        if (MockDataCategory is null || MockDataField is null) return null;
        return MockDataCatalog.All.FirstOrDefault(e =>
            e.Category == MockDataCategory && e.Field == MockDataField);
    }

    /// <summary>Well-known variable type constants.</summary>
    public static class VariableTypes
    {
        /// <summary>Plain string value — used directly in substitution.</summary>
        public const string Static = "static";

        /// <summary>Reserved: JavaScript expression evaluated at request send time.</summary>
        public const string Script = "script";

        /// <summary>Reserved: Extracted from the response of another named request.</summary>
        public const string Chained = "chained";

        /// <summary>
        /// Legacy composite segment-based type — migrated to <see cref="MockData"/> or
        /// <see cref="ResponseBody"/> on load. Do not use for new variables.
        /// </summary>
        public const string Dynamic = "dynamic";

        /// <summary>
        /// Generates a fresh fake value via Bogus each time the variable is referenced.
        /// Configured by <see cref="EnvironmentVariable.MockDataCategory"/> and <see cref="EnvironmentVariable.MockDataField"/>.
        /// </summary>
        public const string MockData = "mock-data";

        /// <summary>
        /// Value extracted from a collection request's response body at send time.
        /// Configured by <see cref="EnvironmentVariable.ResponseRequestName"/>,
        /// <see cref="EnvironmentVariable.ResponsePath"/>, <see cref="EnvironmentVariable.ResponseFrequency"/>,
        /// and optionally <see cref="EnvironmentVariable.ResponseExpiresAfterSeconds"/>.
        /// </summary>
        public const string ResponseBody = "response-body";
    }
}
