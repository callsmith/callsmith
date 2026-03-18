using System.Text;
using System.Text.RegularExpressions;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Converts between the <see cref="ValueSegment"/> list representation and the
/// compact inline-text format used for persistence and send-time evaluation.
///
/// <para><strong>Inline text grammar:</strong></para>
/// <list type="bullet">
///   <item><c>{{varName}}</c> — reference to an environment variable (unchanged)</item>
///   <item><c>{% faker 'Category.Field' %}</c> — mock data generator</item>
///   <item><c>{% response 'body', 'requestName', 'jsonPath', 'frequency', seconds %}</c>
///         — chained response extraction</item>
/// </list>
/// </summary>
public static partial class SegmentSerializer
{
    // ── Parse ─────────────────────────────────────────────────────────────────

    // Matches {% faker 'Category.Field' %} — with optional whitespace
    [GeneratedRegex(@"\{%-?\s*faker\s+'([^']+)'\s*-?%\}", RegexOptions.Compiled)]
    private static partial Regex FakerTag();

    // Matches {% response 'body', 'reqName/path', 'jsonPath', 'frequency'[, seconds] %}
    [GeneratedRegex(
        @"\{%-?\s*response\s+'([^']*)'\s*,\s*'([^']*)'\s*,\s*'([^']*)'\s*,\s*'([^']*)'(?:\s*,\s*(\d+))?\s*-?%\}",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex ResponseTag();

    // Used to split on any {% %} block so we can walk static segments between them
    [GeneratedRegex(@"(\{%.*?%\})", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex AnyTag();

    /// <summary>
    /// Parses an inline-format string into a list of <see cref="ValueSegment"/>s.
    /// Returns <see langword="null"/> when the string contains no dynamic tokens
    /// (i.e. it is purely static or references only <c>{{varName}}</c> env vars).
    /// </summary>
    public static IReadOnlyList<ValueSegment>? ParseToSegments(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!value.Contains("{%", StringComparison.Ordinal)) return null;

        var parts = AnyTag().Split(value);
        var segments = new List<ValueSegment>();

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            var fakerMatch = FakerTag().Match(part);
            if (fakerMatch.Success)
            {
                var key = fakerMatch.Groups[1].Value; // e.g. "Name.First Name"
                var (cat, field) = SplitFakerKey(key);
                segments.Add(new MockDataSegment { Category = cat, Field = field });
                continue;
            }

            var responseMatch = ResponseTag().Match(part);
            if (responseMatch.Success)
            {
                var frequency = ParseFrequency(responseMatch.Groups[4].Value);
                int? expires = int.TryParse(responseMatch.Groups[5].Value, out var s) ? s : null;
                segments.Add(new DynamicValueSegment
                {
                    RequestName = responseMatch.Groups[2].Value,
                    Path = responseMatch.Groups[3].Value,
                    Frequency = frequency,
                    ExpiresAfterSeconds = expires,
                });
                continue;
            }

            // Everything else (plain text + {{varName}} tokens) is a static segment.
            segments.Add(new StaticValueSegment { Text = part });
        }

        // Simplify: if only one static segment, treat as pure static string.
        if (segments.Count == 1 && segments[0] is StaticValueSegment)
            return null;

        return segments.Count > 0 ? segments : null;
    }

    // ── Serialize ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a list of <see cref="ValueSegment"/>s back to inline-text format.
    /// </summary>
    public static string SerializeSegments(IReadOnlyList<ValueSegment>? segments)
    {
        if (segments is null or { Count: 0 }) return string.Empty;

        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            switch (seg)
            {
                case StaticValueSegment s:
                    sb.Append(s.Text);
                    break;
                case MockDataSegment m:
                    sb.Append($"{{% faker '{MockDataKey(m.Category, m.Field)}' %}}");
                    break;
                case DynamicValueSegment d:
                    var freq = SerializeFrequency(d.Frequency);
                    if (d.ExpiresAfterSeconds.HasValue)
                        sb.Append($"{{% response 'body', '{d.RequestName}', '{d.Path}', '{freq}', {d.ExpiresAfterSeconds} %}}");
                    else
                        sb.Append($"{{% response 'body', '{d.RequestName}', '{d.Path}', '{freq}' %}}");
                    break;
            }
        }
        return sb.ToString();
    }

    // ── Faker key helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the canonical inline key for a mock-data segment: <c>"Category.Field"</c>.
    /// </summary>
    public static string MockDataKey(string category, string field) => $"{category}.{field}";

    private static (string category, string field) SplitFakerKey(string key)
    {
        // "Name.First Name" → ("Name", "First Name")
        // Try period-separated first, then fall back to camelCase / Bogus-style lookup
        var dotIdx = key.IndexOf('.', StringComparison.Ordinal);
        if (dotIdx > 0)
        {
            var cat = key[..dotIdx];
            var fld = key[(dotIdx + 1)..];
            // Verify exists in catalog; if not, try the Bogus-name lookup
            if (MockDataCatalog.All.Any(e =>
                    string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Field, fld, StringComparison.OrdinalIgnoreCase)))
            {
                // Canonical case from catalog
                var canonical = MockDataCatalog.All.First(e =>
                    string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Field, fld, StringComparison.OrdinalIgnoreCase));
                return (canonical.Category, canonical.Field);
            }
        }

        // Fall back: try matching by Bogus method name (camelCase → catalog entry)
        var entry = MockDataCatalog.FindByBogusName(key);
        return entry is not null ? (entry.Category, entry.Field) : ("Random", "UUID");
    }

    private static DynamicFrequency ParseFrequency(string raw) =>
        raw switch
        {
            "always" => DynamicFrequency.Always,
            "when-expired" => DynamicFrequency.IfExpired,
            "never" => DynamicFrequency.Never,
            _ => DynamicFrequency.Always,
        };

    private static string SerializeFrequency(DynamicFrequency freq) =>
        freq switch
        {
            DynamicFrequency.Always => "always",
            DynamicFrequency.IfExpired => "when-expired",
            DynamicFrequency.Never => "never",
            _ => "always",
        };
}
