using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances used across all Callsmith file services.
/// Centralises the serialisation configuration to keep it consistent and DRY.
/// </summary>
public static class CallsmithJsonOptions
{
    /// <summary>
    /// The default options for persisting Callsmith data files:
    /// indented, camelCase property names, nulls omitted.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
