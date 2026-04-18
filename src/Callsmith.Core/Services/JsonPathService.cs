using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Callsmith.Core.Abstractions;

namespace Callsmith.Core.Services;

/// <summary>
/// RFC 9535 compliant JSONPath query engine.
/// Supports all selector types: name, index, wildcard, slice, filter, and descendant segments.
/// </summary>
public sealed class JsonPathService : IJsonPathService
{
    /// <inheritdoc/>
    public IReadOnlyList<JsonElement> Query(JsonElement root, string expression)
    {
        if (!TryParseExpression(expression, out var steps, out _))
            return [];

        if (!TryEvaluateSteps([root], root, steps, out var result, out _))
            return [];
        return result;
    }

    /// <inheritdoc/>
    public bool TryValidate(string expression, out string error)
        => TryParseExpression(expression, out _, out error);

    /// <inheritdoc/>
    public bool TryQuery(JsonElement root, string expression,
        out IReadOnlyList<JsonElement> results, out string error)
    {
        results = [];
        if (!TryParseExpression(expression, out var steps, out error))
            return false;

        if (!TryEvaluateSteps([root], root, steps, out var list, out error))
            return false;

        results = list;
        return true;
    }

    // ─── Evaluator ────────────────────────────────────────────────────────────

    /// <summary>Evaluates a mixed list of path steps (selectors and/or sort operations).</summary>
    private static bool TryEvaluateSteps(
        List<JsonElement> nodes, JsonElement root, IReadOnlyList<IPathStep> steps,
        out List<JsonElement> result, out string error)
    {
        result = nodes;
        error = string.Empty;

        foreach (var step in steps)
        {
            var next = new List<JsonElement>();

            if (step is Segment segment)
            {
                foreach (var node in result)
                {
                    if (segment.IsDescendant)
                        CollectDescendants(node, root, segment.Selectors, next);
                    else
                        ApplySelectors(node, root, segment.Selectors, next);
                }
            }
            else if (step is SortStep sort)
            {
                if (!TryApplySort(result, sort, next, out error))
                    return false;
            }
            else if (step is DistinctStep distinct)
            {
                if (!TryApplyDistinct(result, distinct, next, out error))
                    return false;
            }

            result = next;
        }

        return true;
    }

    /// <summary>Evaluates a list of regular segments (used inside filter-path expressions).</summary>
    private static List<JsonElement> EvaluateSegments(
        List<JsonElement> nodes, JsonElement root, IReadOnlyList<Segment> segments)
    {
        foreach (var segment in segments)
        {
            var next = new List<JsonElement>();
            foreach (var node in nodes)
            {
                if (segment.IsDescendant)
                    CollectDescendants(node, root, segment.Selectors, next);
                else
                    ApplySelectors(node, root, segment.Selectors, next);
            }
            nodes = next;
        }

        return nodes;
    }

    private static void ApplySelectors(
        JsonElement node, JsonElement root, IReadOnlyList<ISelector> selectors, List<JsonElement> results)
    {
        foreach (var selector in selectors)
            selector.Apply(node, root, results);
    }

    private static void CollectDescendants(
        JsonElement node, JsonElement root, IReadOnlyList<ISelector> selectors, List<JsonElement> results)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(node);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            ApplySelectors(current, root, selectors, results);

            if (current.ValueKind == JsonValueKind.Object)
            {
                var children = new List<JsonElement>();
                foreach (var prop in current.EnumerateObject())
                    children.Add(prop.Value);
                for (var i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                var children = new List<JsonElement>();
                foreach (var item in current.EnumerateArray())
                    children.Add(item);
                for (var i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }
        }
    }

    // ─── Sort helpers ─────────────────────────────────────────────────────────

    private static bool TryApplySort(
        List<JsonElement> nodes, SortStep sort, List<JsonElement> output, out string error)
    {
        error = string.Empty;

        if (nodes.Count == 0)
            return true;

        // When no node is itself a JSON array, the elements were already expanded by a
        // wildcard or slice selector (e.g. [*] or [0:5]). Treat the whole list as one
        // flat virtual array and sort it in one pass.
        bool flatListMode = nodes.TrueForAll(n => n.ValueKind != JsonValueKind.Array);

        if (flatListMode)
            return TrySortElements(nodes, sort, output, out error);

        // Per-array mode: each node must be an array; sort each one independently.
        foreach (var node in nodes)
        {
            if (node.ValueKind != JsonValueKind.Array)
            {
                error = "sort functions can only be applied to arrays.";
                return false;
            }

            var elements = node.EnumerateArray().ToList();
            if (!TrySortElements(elements, sort, output, out error))
                return false;
        }

        return true;
    }

    private static bool TrySortElements(
        List<JsonElement> elements, SortStep sort, List<JsonElement> output, out string error)
    {
        error = string.Empty;

        if (elements.Count == 0)
            return true; // empty → nothing to add

        // Determine element type from the first non-null element
        JsonElement? firstNonNull = null;
        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Null)
            {
                firstNonNull = el;
                break;
            }
        }

        var isObjectArray = firstNonNull?.ValueKind == JsonValueKind.Object;

        if (isObjectArray && sort.SortExpression is null)
        {
            error = "sort requires a property expression for arrays of objects, e.g. sort_asc(name).";
            return false;
        }

        if (!isObjectArray && sort.SortExpression is not null)
        {
            error = "sort cannot use a property expression for arrays of primitives.";
            return false;
        }

        var sorted = sort.Ascending
            ? elements.OrderBy(e => GetSortKey(e, sort.SortExpression), SortKeyComparer.Instance)
            : elements.OrderByDescending(e => GetSortKey(e, sort.SortExpression), SortKeyComparer.Instance);

        output.AddRange(sorted);
        return true;
    }

    private static bool TryApplyDistinct(
        List<JsonElement> nodes, DistinctStep distinct, List<JsonElement> output, out string error)
    {
        error = string.Empty;

        if (nodes.Count == 0)
            return true;

        // Mirrors sort behavior: if nodes have already been expanded into a flat list,
        // treat them as one virtual array.
        bool flatListMode = nodes.TrueForAll(n => n.ValueKind != JsonValueKind.Array);

        if (flatListMode)
            return TryDistinctElements(nodes, distinct, output, out error);

        // Per-array mode: each node must be an array; de-duplicate each one independently.
        foreach (var node in nodes)
        {
            if (node.ValueKind != JsonValueKind.Array)
            {
                error = "distinct can only be applied to arrays.";
                return false;
            }

            var elements = node.EnumerateArray().ToList();
            if (!TryDistinctElements(elements, distinct, output, out error))
                return false;
        }

        return true;
    }

    private static bool TryDistinctElements(
        List<JsonElement> elements, DistinctStep distinct, List<JsonElement> output, out string error)
    {
        error = string.Empty;

        if (elements.Count == 0)
            return true;

        // Determine element type from the first non-null element.
        JsonElement? firstNonNull = null;
        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Null)
            {
                firstNonNull = el;
                break;
            }
        }

        var isObjectArray = firstNonNull?.ValueKind == JsonValueKind.Object;

        if (isObjectArray && distinct.DistinctExpression is null)
        {
            error = "distinct requires a property expression for arrays of objects, e.g. distinct(name).";
            return false;
        }

        if (!isObjectArray && distinct.DistinctExpression is not null)
        {
            error = "distinct cannot use a property expression for arrays of primitives.";
            return false;
        }

        if (isObjectArray)
        {
            output.AddRange(elements.DistinctBy(
                element => GetSortKey(element, distinct.DistinctExpression),
                SortKeyEqualityComparer.Instance));
        }
        else
        {
            output.AddRange(elements.Distinct(JsonElementValueComparer.Instance));
        }

        return true;
    }

    private static object? GetSortKey(JsonElement element, string? propertyName)
    {
        var target = element;
        if (propertyName is not null)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out target))
                return null; // missing property → sorts last
        }

        return target.ValueKind switch
        {
            JsonValueKind.String => target.GetString(),
            JsonValueKind.Number => target.TryGetDouble(out var d) ? (object?)d : target.GetRawText(),
            JsonValueKind.True => (object?)1.0,
            JsonValueKind.False => (object?)0.0,
            _ => null,
        };
    }

    private sealed class SortKeyComparer : IComparer<object?>
    {
        public static readonly SortKeyComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return 1;  // nulls sort last
            if (y is null) return -1;

            if (x is double dx && y is double dy)
                return dx.CompareTo(dy);

            if (x is string sx && y is string sy)
                return string.CompareOrdinal(sx, sy);

            // Mixed types: fall back to string comparison
            return string.CompareOrdinal(x.ToString() ?? string.Empty, y.ToString() ?? string.Empty);
        }
    }

    private sealed class SortKeyEqualityComparer : IEqualityComparer<object?>
    {
        public static readonly SortKeyEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is null && y is null) return true;
            if (x is null || y is null) return false;

            // Only treat values as equal when they are the same runtime type,
            // so numeric 1 and string "1" are never considered duplicates.
            if (x is double dx && y is double dy)
                return dx.Equals(dy);

            if (x is string sx && y is string sy)
                return string.Equals(sx, sy, StringComparison.Ordinal);

            // Different types (e.g. double vs string) are never equal.
            return false;
        }

        public int GetHashCode(object? obj)
        {
            if (obj is null) return 0;
            // Include the runtime type in the hash so values of different types
            // land in different buckets even when their string forms are identical.
            if (obj is double d) return HashCode.Combine(typeof(double), d);
            if (obj is string s) return HashCode.Combine(typeof(string), StringComparer.Ordinal.GetHashCode(s));
            return HashCode.Combine(obj.GetType(), obj.GetHashCode());
        }
    }

    private sealed class JsonElementValueComparer : IEqualityComparer<JsonElement>
    {
        public static readonly JsonElementValueComparer Instance = new();

        public bool Equals(JsonElement x, JsonElement y)
        {
            if (x.ValueKind != y.ValueKind)
                return false;

            return x.ValueKind switch
            {
                JsonValueKind.String => string.Equals(x.GetString(), y.GetString(), StringComparison.Ordinal),
                JsonValueKind.Number => NumbersEqual(x, y),
                JsonValueKind.True => true,
                JsonValueKind.False => true,
                JsonValueKind.Null => true,
                JsonValueKind.Undefined => true,
                _ => string.Equals(x.GetRawText(), y.GetRawText(), StringComparison.Ordinal),
            };
        }

        public int GetHashCode(JsonElement obj)
        {
            return obj.ValueKind switch
            {
                JsonValueKind.String => HashCode.Combine(JsonValueKind.String, StringComparer.Ordinal.GetHashCode(obj.GetString() ?? string.Empty)),
                JsonValueKind.Number => HashCode.Combine(JsonValueKind.Number, GetNumberHashCode(obj)),
                JsonValueKind.True => JsonValueKind.True.GetHashCode(),
                JsonValueKind.False => JsonValueKind.False.GetHashCode(),
                JsonValueKind.Null => JsonValueKind.Null.GetHashCode(),
                JsonValueKind.Undefined => JsonValueKind.Undefined.GetHashCode(),
                _ => HashCode.Combine(obj.ValueKind, StringComparer.Ordinal.GetHashCode(obj.GetRawText())),
            };
        }

        private static bool NumbersEqual(JsonElement x, JsonElement y)
        {
            if (x.TryGetDecimal(out var dx) && y.TryGetDecimal(out var dy))
                return dx == dy;

            return x.GetDouble().Equals(y.GetDouble());
        }

        private static int GetNumberHashCode(JsonElement value)
        {
            // Cast decimal to double before hashing. NumbersEqual falls back to double
            // when either operand cannot be represented as decimal, so a decimal value
            // that equals a non-decimal value via double comparison must produce the same
            // hash code — which requires normalising through double here as well.
            if (value.TryGetDecimal(out var d))
                return ((double)d).GetHashCode();

            return value.GetDouble().GetHashCode();
        }
    }

    // ─── Parser ───────────────────────────────────────────────────────────────

    private static bool TryParseExpression(
        string input, out IReadOnlyList<IPathStep> steps, out string error)
    {
        steps = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Path cannot be empty.";
            return false;
        }

        var span = input.AsSpan().Trim();
        if (span[0] != '$')
        {
            error = "JSONPath must start with '$'.";
            return false;
        }

        var pos = 1;
        var result = new List<IPathStep>();

        while (pos < span.Length)
        {
            if (!TryParseNextStep(span, ref pos, out var step, out error))
                return false;
            result.Add(step);
        }

        steps = result;
        return true;
    }

    /// <summary>
    /// Parses one path step from the main query, returning either a <see cref="Segment"/>,
    /// <see cref="SortStep"/>, or <see cref="DistinctStep"/> for extension function calls.
    /// </summary>
    private static bool TryParseNextStep(
        ReadOnlySpan<char> span, ref int pos, out IPathStep step, out string error)
    {
        step = new Segment(false, []);
        error = string.Empty;

        if (pos >= span.Length)
        {
            error = "Unexpected end of path.";
            return false;
        }

        // Non-dot bracket segment
        if (span[pos] == '[')
        {
            if (!TryParseBracketedSelection(span, ref pos, out var selectors, out error))
                return false;
            step = new Segment(false, selectors);
            return true;
        }

        if (span[pos] != '.')
        {
            error = $"Expected '.' or '[' at position {pos}.";
            return false;
        }

        pos++; // skip first '.'

        bool isDescendant = false;
        if (pos < span.Length && span[pos] == '.')
        {
            isDescendant = true;
            pos++; // skip second '.'
        }

        if (pos >= span.Length)
        {
            error = "Unexpected end of path after '.'.";
            return false;
        }

        if (span[pos] == '[')
        {
            if (!TryParseBracketedSelection(span, ref pos, out var selectors, out error))
                return false;
            step = new Segment(isDescendant, selectors);
            return true;
        }

        if (span[pos] == '*')
        {
            pos++;
            step = new Segment(isDescendant, [WildcardSelector.Instance]);
            return true;
        }

        if (!TryParseMemberName(span, ref pos, out var name, out error))
            return false;

        // Detect extension function calls: sort(...), sort_asc(...), sort_desc(...), distinct(...)
        if (name is "sort" or "sort_asc" or "sort_desc" or "distinct")
        {
            SkipWhitespace(span, ref pos);
            if (pos < span.Length && span[pos] == '(')
            {
                if (isDescendant)
                {
                    error = "extension functions cannot be used with the descendant operator '..'.";
                    return false;
                }

                pos++; // skip '('
                SkipWhitespace(span, ref pos);

                string? sortExpr = null;
                if (pos < span.Length && span[pos] != ')')
                {
                    if (span[pos] == '\'' || span[pos] == '"')
                    {
                        if (!TryParseQuotedString(span, ref pos, out sortExpr, out error))
                            return false;
                    }
                    else if (IsNameFirst(span[pos]))
                    {
                        var exprStart = pos;
                        while (pos < span.Length && IsNameChar(span[pos])) pos++;
                        sortExpr = span[exprStart..pos].ToString();
                    }
                    else
                    {
                        error = $"Invalid expression at position {pos}.";
                        return false;
                    }

                    SkipWhitespace(span, ref pos);
                }

                if (pos >= span.Length || span[pos] != ')')
                {
                    error = "Missing ')' in extension function call.";
                    return false;
                }

                pos++; // skip ')'
                step = name == "distinct"
                    ? new DistinctStep(sortExpr)
                    : new SortStep(Ascending: name != "sort_desc", SortExpression: sortExpr);
                return true;
            }
        }

        step = new Segment(isDescendant, [new NameSelector(name)]);
        return true;
    }

    private static bool TryParseSegment(
        ReadOnlySpan<char> span, ref int pos, out Segment segment, out string error)
    {
        segment = new Segment(false, []);
        error = string.Empty;

        if (pos >= span.Length)
        {
            error = "Unexpected end of path.";
            return false;
        }

        if (span[pos] == '[')
        {
            if (!TryParseBracketedSelection(span, ref pos, out var selectors, out error))
                return false;
            segment = new Segment(false, selectors);
            return true;
        }

        if (span[pos] != '.')
        {
            error = $"Expected '.' or '[' at position {pos}.";
            return false;
        }

        pos++; // skip first '.'

        bool isDescendant = false;
        if (pos < span.Length && span[pos] == '.')
        {
            isDescendant = true;
            pos++; // skip second '.'
        }

        if (pos >= span.Length)
        {
            error = "Unexpected end of path after '.'.";
            return false;
        }

        if (span[pos] == '[')
        {
            if (!TryParseBracketedSelection(span, ref pos, out var selectors, out error))
                return false;
            segment = new Segment(isDescendant, selectors);
            return true;
        }

        if (span[pos] == '*')
        {
            pos++;
            segment = new Segment(isDescendant, [WildcardSelector.Instance]);
            return true;
        }

        if (!TryParseMemberName(span, ref pos, out var name, out error))
            return false;

        segment = new Segment(isDescendant, [new NameSelector(name)]);
        return true;
    }

    private static bool TryParseBracketedSelection(
        ReadOnlySpan<char> span, ref int pos, out IReadOnlyList<ISelector> selectors, out string error)
    {
        selectors = [];
        error = string.Empty;
        pos++; // skip '['

        SkipWhitespace(span, ref pos);

        if (pos >= span.Length)
        {
            error = "Unexpected end of input inside '['.";
            return false;
        }

        if (span[pos] == ']')
        {
            error = "Empty bracket selector is not valid.";
            return false;
        }

        var result = new List<ISelector>();
        while (true)
        {
            SkipWhitespace(span, ref pos);

            if (!TryParseSelector(span, ref pos, out var sel, out error))
                return false;

            result.Add(sel);
            SkipWhitespace(span, ref pos);

            if (pos >= span.Length)
            {
                error = "Missing closing ']'.";
                return false;
            }

            if (span[pos] == ']') { pos++; break; }

            if (span[pos] != ',')
            {
                error = $"Expected ',' or ']' at position {pos}.";
                return false;
            }

            pos++; // skip ','
        }

        selectors = result;
        return true;
    }

    private static bool TryParseSelector(
        ReadOnlySpan<char> span, ref int pos, out ISelector selector, out string error)
    {
        selector = WildcardSelector.Instance;
        error = string.Empty;

        if (pos >= span.Length)
        {
            error = "Unexpected end of input in selector.";
            return false;
        }

        var ch = span[pos];

        if (ch == '*') { pos++; selector = WildcardSelector.Instance; return true; }

        if (ch == '?')
        {
            pos++;
            SkipWhitespace(span, ref pos);
            if (!TryParseFilterExpr(span, ref pos, out var fe, out error))
                return false;
            selector = new FilterSelector(fe);
            return true;
        }

        if (ch == '\'' || ch == '"')
        {
            if (!TryParseQuotedString(span, ref pos, out var name, out error))
                return false;
            selector = new NameSelector(name);
            return true;
        }

        if (ch == '-' || char.IsAsciiDigit(ch) || ch == ':')
            return TryParseIndexOrSlice(span, ref pos, out selector, out error);

        error = $"Unexpected character '{ch}' in selector at position {pos}.";
        return false;
    }

    private static bool TryParseIndexOrSlice(
        ReadOnlySpan<char> span, ref int pos, out ISelector selector, out string error)
    {
        selector = WildcardSelector.Instance;
        error = string.Empty;

        // Handle bare colon at start e.g. [::2] or [:3]
        if (pos < span.Length && span[pos] == ':')
        {
            pos++;
            int? end = null;
            if (pos < span.Length && (span[pos] == '-' || char.IsAsciiDigit(span[pos])))
            {
                if (!TryParseInt(span, ref pos, out var n, out error)) return false;
                end = n;
            }
            int? step = null;
            if (pos < span.Length && span[pos] == ':')
            {
                pos++;
                if (pos < span.Length && (span[pos] == '-' || char.IsAsciiDigit(span[pos])))
                {
                    if (!TryParseInt(span, ref pos, out var n, out error)) return false;
                    step = n;
                }
            }
            selector = new SliceSelector(null, end, step);
            return true;
        }

        if (!TryParseInt(span, ref pos, out var start, out error))
            return false;

        // Check for slice separator
        if (pos < span.Length && span[pos] == ':')
        {
            pos++;
            int? end = null;
            if (pos < span.Length && (span[pos] == '-' || char.IsAsciiDigit(span[pos])))
            {
                if (!TryParseInt(span, ref pos, out var n, out error)) return false;
                end = n;
            }
            int? step = null;
            if (pos < span.Length && span[pos] == ':')
            {
                pos++;
                if (pos < span.Length && (span[pos] == '-' || char.IsAsciiDigit(span[pos])))
                {
                    if (!TryParseInt(span, ref pos, out var n, out error)) return false;
                    step = n;
                }
            }
            selector = new SliceSelector(start, end, step);
            return true;
        }

        selector = new IndexSelector(start);
        return true;
    }

    private static bool TryParseInt(
        ReadOnlySpan<char> span, ref int pos, out int value, out string error)
    {
        value = 0;
        error = string.Empty;
        var start = pos;

        if (pos < span.Length && span[pos] == '-') pos++;

        if (pos >= span.Length || !char.IsAsciiDigit(span[pos]))
        {
            error = $"Expected digit at position {pos}.";
            pos = start;
            return false;
        }

        // RFC 9535: no leading zeros (except for 0 itself)
        if (span[pos] == '0' && pos + 1 < span.Length && char.IsAsciiDigit(span[pos + 1]))
        {
            error = $"Leading zeros are not allowed in integer at position {pos}.";
            return false;
        }

        while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;

        if (!int.TryParse(span[start..pos], out value))
        {
            error = $"Integer value out of range at position {start}.";
            return false;
        }

        return true;
    }

    private static bool TryParseMemberName(
        ReadOnlySpan<char> span, ref int pos, out string name, out string error)
    {
        name = string.Empty;
        error = string.Empty;
        var start = pos;

        if (pos >= span.Length || !IsNameFirst(span[pos]))
        {
            error = $"Expected member name at position {pos}.";
            return false;
        }

        while (pos < span.Length && IsNameChar(span[pos])) pos++;

        name = span[start..pos].ToString();
        return true;
    }

    private static bool IsNameFirst(char c) => char.IsLetter(c) || c == '_' || c > '\u007F';

    private static bool IsNameChar(char c) => IsNameFirst(c) || char.IsAsciiDigit(c);

    private static bool TryParseQuotedString(
        ReadOnlySpan<char> span, ref int pos, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        var quote = span[pos];
        pos++;

        var sb = new StringBuilder();
        while (pos < span.Length && span[pos] != quote)
        {
            if (span[pos] == '\\')
            {
                pos++;
                if (pos >= span.Length) { error = "Unexpected end of string escape."; return false; }

                switch (span[pos])
                {
                    case 'b': sb.Append('\b'); pos++; break;
                    case 'f': sb.Append('\f'); pos++; break;
                    case 'n': sb.Append('\n'); pos++; break;
                    case 'r': sb.Append('\r'); pos++; break;
                    case 't': sb.Append('\t'); pos++; break;
                    case '/': sb.Append('/'); pos++; break;
                    case '\\': sb.Append('\\'); pos++; break;
                    case '\'': sb.Append('\''); pos++; break;
                    case '"': sb.Append('"'); pos++; break;
                    case 'u':
                        pos++;
                        if (pos + 4 > span.Length) { error = "Invalid unicode escape."; return false; }
                        var hex = span[pos..(pos + 4)].ToString();
                        if (!int.TryParse(hex, NumberStyles.HexNumber, null, out var code))
                        {
                            error = $"Invalid unicode escape \\u{hex}.";
                            return false;
                        }
                        sb.Append((char)code);
                        pos += 4;
                        break;
                    default:
                        error = $"Invalid escape sequence '\\{span[pos]}' at position {pos}.";
                        return false;
                }
            }
            else
            {
                sb.Append(span[pos]);
                pos++;
            }
        }

        if (pos >= span.Length)
        {
            error = "Unterminated string literal.";
            return false;
        }

        pos++; // skip closing quote
        value = sb.ToString();
        return true;
    }

    private static void SkipWhitespace(ReadOnlySpan<char> span, ref int pos)
    {
        while (pos < span.Length && span[pos] is ' ' or '\t' or '\n' or '\r') pos++;
    }

    // ─── Filter expression parser ─────────────────────────────────────────────

    private static bool TryParseFilterExpr(
        ReadOnlySpan<char> span, ref int pos, out FilterExpr expr, out string error)
        => TryParseOrExpr(span, ref pos, out expr, out error);

    private static bool TryParseOrExpr(
        ReadOnlySpan<char> span, ref int pos, out FilterExpr expr, out string error)
    {
        if (!TryParseAndExpr(span, ref pos, out expr, out error)) return false;

        SkipWhitespace(span, ref pos);
        while (pos + 1 < span.Length && span[pos] == '|' && span[pos + 1] == '|')
        {
            pos += 2;
            SkipWhitespace(span, ref pos);
            if (!TryParseAndExpr(span, ref pos, out var rhs, out error)) return false;
            expr = new OrExpr(expr, rhs);
            SkipWhitespace(span, ref pos);
        }

        return true;
    }

    private static bool TryParseAndExpr(
        ReadOnlySpan<char> span, ref int pos, out FilterExpr expr, out string error)
    {
        if (!TryParseUnaryExpr(span, ref pos, out expr, out error)) return false;

        SkipWhitespace(span, ref pos);
        while (pos + 1 < span.Length && span[pos] == '&' && span[pos + 1] == '&')
        {
            pos += 2;
            SkipWhitespace(span, ref pos);
            if (!TryParseUnaryExpr(span, ref pos, out var rhs, out error)) return false;
            expr = new AndExpr(expr, rhs);
            SkipWhitespace(span, ref pos);
        }

        return true;
    }

    private static bool TryParseUnaryExpr(
        ReadOnlySpan<char> span, ref int pos, out FilterExpr expr, out string error)
    {
        expr = LiteralBoolExpr.False;
        error = string.Empty;

        SkipWhitespace(span, ref pos);

        if (pos < span.Length && span[pos] == '!')
        {
            pos++;
            SkipWhitespace(span, ref pos);
            if (!TryParseUnaryExpr(span, ref pos, out var inner, out error)) return false;
            expr = new NotExpr(inner);
            return true;
        }

        return TryParseParenOrAtom(span, ref pos, out expr, out error);
    }

    private static bool TryParseParenOrAtom(
        ReadOnlySpan<char> span, ref int pos, out FilterExpr expr, out string error)
    {
        expr = LiteralBoolExpr.False;
        error = string.Empty;

        if (pos < span.Length && span[pos] == '(')
        {
            pos++;
            SkipWhitespace(span, ref pos);
            if (!TryParseOrExpr(span, ref pos, out expr, out error)) return false;
            SkipWhitespace(span, ref pos);
            if (pos >= span.Length || span[pos] != ')')
            {
                error = $"Expected ')' at position {pos}.";
                return false;
            }
            pos++;
            return true;
        }

        return TryParseComparisonOrTest(span, ref pos, out expr, out error);
    }

    private static bool TryParseComparisonOrTest(
        ReadOnlySpan<char> span, ref int pos, out FilterExpr expr, out string error)
    {
        expr = LiteralBoolExpr.False;
        error = string.Empty;

        if (!TryParseComparableValue(span, ref pos, out var left, out error))
            return false;

        SkipWhitespace(span, ref pos);

        var cmpOp = TryReadComparisonOp(span, ref pos);
        if (cmpOp is not null)
        {
            SkipWhitespace(span, ref pos);
            if (!TryParseComparableValue(span, ref pos, out var right, out error))
                return false;
            expr = new ComparisonExpr(left, cmpOp, right);
            return true;
        }

        // No comparison operator — must be a test expression (existence or function)
        if (left is PathComparable pc)
        {
            expr = new ExistenceTestExpr(pc);
            return true;
        }

        if (left is FunctionCallComparable fc)
        {
            expr = new FunctionTestExpr(fc);
            return true;
        }

        error = $"Expected comparison operator at position {pos}.";
        return false;
    }

    private static bool TryParseComparableValue(
        ReadOnlySpan<char> span, ref int pos, out IComparableValue comparable, out string error)
    {
        comparable = NullLiteral.Instance;
        error = string.Empty;

        if (pos >= span.Length)
        {
            error = "Unexpected end of filter expression.";
            return false;
        }

        var ch = span[pos];

        // Filter-query: @ (relative) or $ (absolute)
        if (ch == '@' || ch == '$')
        {
            var isRelative = ch == '@';
            pos++;
            var segs = new List<Segment>();
            while (pos < span.Length && (span[pos] == '.' || span[pos] == '['))
            {
                if (!TryParseSegment(span, ref pos, out var seg, out error))
                    return false;
                segs.Add(seg);
            }
            comparable = new PathComparable(isRelative, segs);
            return true;
        }

        // String literal
        if (ch == '\'' || ch == '"')
        {
            if (!TryParseQuotedString(span, ref pos, out var str, out error))
                return false;
            comparable = new StringLiteral(str);
            return true;
        }

        // Number literal
        if (ch == '-' || char.IsAsciiDigit(ch))
        {
            if (!TryParseNumberLiteral(span, ref pos, out var num, out error))
                return false;
            comparable = new NumberLiteral(num);
            return true;
        }

        // Identifier: true / false / null / function call
        if (char.IsLetter(ch) || ch == '_')
        {
            var identStart = pos;
            while (pos < span.Length && (char.IsLetterOrDigit(span[pos]) || span[pos] == '_'))
                pos++;

            var ident = span[identStart..pos].ToString();

            if (ident == "true") { comparable = BoolLiteral.True; return true; }
            if (ident == "false") { comparable = BoolLiteral.False; return true; }
            if (ident == "null") { comparable = NullLiteral.Instance; return true; }

            SkipWhitespace(span, ref pos);
            if (pos < span.Length && span[pos] == '(')
            {
                pos++; // skip '('
                var args = new List<IComparableValue>();
                SkipWhitespace(span, ref pos);

                if (pos < span.Length && span[pos] != ')')
                {
                    while (true)
                    {
                        SkipWhitespace(span, ref pos);
                        if (!TryParseComparableValue(span, ref pos, out var arg, out error))
                            return false;
                        args.Add(arg);
                        SkipWhitespace(span, ref pos);
                        if (pos >= span.Length) { error = "Missing ')' in function call."; return false; }
                        if (span[pos] == ')') break;
                        if (span[pos] != ',')
                        {
                            error = $"Expected ',' in function arguments at position {pos}.";
                            return false;
                        }
                        pos++;
                    }
                }

                if (pos >= span.Length || span[pos] != ')')
                {
                    error = "Missing ')' in function call.";
                    return false;
                }
                pos++;
                comparable = new FunctionCallComparable(ident, args);
                return true;
            }

            error = $"Unknown identifier '{ident}' in filter expression.";
            return false;
        }

        error = $"Unexpected character '{ch}' in filter expression at position {pos}.";
        return false;
    }

    private static bool TryParseNumberLiteral(
        ReadOnlySpan<char> span, ref int pos, out double value, out string error)
    {
        value = 0;
        error = string.Empty;
        var start = pos;

        if (pos < span.Length && span[pos] == '-') pos++;
        while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;

        if (pos < span.Length && span[pos] == '.')
        {
            pos++;
            while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;
        }

        if (pos < span.Length && (span[pos] == 'e' || span[pos] == 'E'))
        {
            pos++;
            if (pos < span.Length && (span[pos] == '+' || span[pos] == '-')) pos++;
            while (pos < span.Length && char.IsAsciiDigit(span[pos])) pos++;
        }

        if (!double.TryParse(
            span[start..pos], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"Invalid number at position {start}.";
            return false;
        }

        return true;
    }

    private static string? TryReadComparisonOp(ReadOnlySpan<char> span, ref int pos)
    {
        if (pos >= span.Length) return null;

        if (pos + 1 < span.Length)
        {
            var two = span.Slice(pos, 2);
            if (two.SequenceEqual("==") || two.SequenceEqual("!=") ||
                two.SequenceEqual("<=") || two.SequenceEqual(">="))
            {
                var op = two.ToString();
                pos += 2;
                return op;
            }
        }

        if (span[pos] == '<') { pos++; return "<"; }
        if (span[pos] == '>') { pos++; return ">"; }
        return null;
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static object? JsonElementToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetDouble(out var d) ? d : (object?)element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element,
        };

    // ─── Data types: path steps (segments and sort operations) ───────────────

    private interface IPathStep { }

    private sealed record Segment(bool IsDescendant, IReadOnlyList<ISelector> Selectors) : IPathStep;

    /// <summary>
    /// Represents a sort function step: <c>sort(expr?)</c>, <c>sort_asc(expr?)</c>, <c>sort_desc(expr?)</c>.
    /// </summary>
    private sealed record SortStep(bool Ascending, string? SortExpression) : IPathStep;

    /// <summary>
    /// Represents a distinct function step: <c>distinct(expr?)</c>.
    /// </summary>
    private sealed record DistinctStep(string? DistinctExpression) : IPathStep;

    private interface ISelector
    {
        void Apply(JsonElement node, JsonElement root, List<JsonElement> results);
    }

    private sealed class WildcardSelector : ISelector
    {
        public static readonly WildcardSelector Instance = new();

        public void Apply(JsonElement node, JsonElement root, List<JsonElement> results)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                    results.Add(prop.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                    results.Add(item);
            }
        }
    }

    private sealed class NameSelector(string name) : ISelector
    {
        public void Apply(JsonElement node, JsonElement root, List<JsonElement> results)
        {
            if (node.ValueKind == JsonValueKind.Object &&
                node.TryGetProperty(name, out var val))
            {
                results.Add(val);
            }
        }
    }

    private sealed class IndexSelector(int index) : ISelector
    {
        public void Apply(JsonElement node, JsonElement root, List<JsonElement> results)
        {
            if (node.ValueKind != JsonValueKind.Array) return;
            var len = node.GetArrayLength();
            var i = index < 0 ? len + index : index;
            if (i >= 0 && i < len)
                results.Add(node[i]);
        }
    }

    private sealed class SliceSelector(int? start, int? end, int? step) : ISelector
    {
        public void Apply(JsonElement node, JsonElement root, List<JsonElement> results)
        {
            if (node.ValueKind != JsonValueKind.Array) return;
            var n = node.GetArrayLength();
            var s = step ?? 1;
            if (s == 0) return;

            if (s > 0)
            {
                var lo = BoundPos(start ?? 0, n);
                var hi = BoundPos(end ?? n, n);
                for (var i = lo; i < hi; i += s)
                    results.Add(node[i]);
            }
            else
            {
                var hi = BoundNeg(start.HasValue ? start.Value : n - 1, n);
                var lo = BoundNeg(end.HasValue ? end.Value : -n - 1, n);
                for (var i = hi; i > lo; i += s)
                    results.Add(node[i]);
            }
        }

        // Normalize index for positive step: clamp to [0, n]
        private static int BoundPos(int i, int n) =>
            i >= 0 ? Math.Min(i, n) : Math.Max(n + i, 0);

        // Normalize index for negative step: clamp to [-1, n-1]
        private static int BoundNeg(int i, int n) =>
            i >= 0 ? Math.Min(i, n - 1) : Math.Max(n + i, -1);
    }

    private sealed class FilterSelector(FilterExpr filter) : ISelector
    {
        public void Apply(JsonElement node, JsonElement root, List<JsonElement> results)
        {
            var ctx = new FilterContext(root);

            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                    if (filter.Evaluate(item, ctx)) results.Add(item);
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                    if (filter.Evaluate(prop.Value, ctx)) results.Add(prop.Value);
            }
        }
    }

    // ─── Data types: filter expressions ──────────────────────────────────────

    private sealed record FilterContext(JsonElement Root);

    private abstract class FilterExpr
    {
        public abstract bool Evaluate(JsonElement current, FilterContext ctx);
    }

    private sealed class LiteralBoolExpr(bool value) : FilterExpr
    {
        public static readonly LiteralBoolExpr False = new(false);
        public override bool Evaluate(JsonElement current, FilterContext ctx) => value;
    }

    private sealed class OrExpr(FilterExpr left, FilterExpr right) : FilterExpr
    {
        public override bool Evaluate(JsonElement current, FilterContext ctx)
            => left.Evaluate(current, ctx) || right.Evaluate(current, ctx);
    }

    private sealed class AndExpr(FilterExpr left, FilterExpr right) : FilterExpr
    {
        public override bool Evaluate(JsonElement current, FilterContext ctx)
            => left.Evaluate(current, ctx) && right.Evaluate(current, ctx);
    }

    private sealed class NotExpr(FilterExpr inner) : FilterExpr
    {
        public override bool Evaluate(JsonElement current, FilterContext ctx)
            => !inner.Evaluate(current, ctx);
    }

    private sealed class ExistenceTestExpr(PathComparable path) : FilterExpr
    {
        public override bool Evaluate(JsonElement current, FilterContext ctx)
            => path.GetNodes(current, ctx).Count > 0;
    }

    private sealed class FunctionTestExpr(FunctionCallComparable func) : FilterExpr
    {
        public override bool Evaluate(JsonElement current, FilterContext ctx)
        {
            var result = func.Evaluate(current, ctx);
            return result is bool b ? b : result is not null;
        }
    }

    private sealed class ComparisonExpr(IComparableValue left, string op, IComparableValue right)
        : FilterExpr
    {
        public override bool Evaluate(JsonElement current, FilterContext ctx)
        {
            var l = left.Evaluate(current, ctx);
            var r = right.Evaluate(current, ctx);
            return Compare(l, op, r);
        }

        private static bool Compare(object? left, string op, object? right)
        {
            if (left is null && right is null) return op is "==" or "<=" or ">=";
            if (left is null || right is null) return op == "!=";

            if (left is bool lb && right is bool rb)
                return op switch { "==" => lb == rb, "!=" => lb != rb, _ => false };

            var ln = ToDouble(left);
            var rn = ToDouble(right);
            if (ln is not null && rn is not null)
                return op switch
                {
                    "==" => ln.Value == rn.Value,
                    "!=" => ln.Value != rn.Value,
                    "<" => ln.Value < rn.Value,
                    "<=" => ln.Value <= rn.Value,
                    ">" => ln.Value > rn.Value,
                    ">=" => ln.Value >= rn.Value,
                    _ => false,
                };

            if (left is string ls && right is string rs)
                return op switch
                {
                    "==" => ls == rs,
                    "!=" => ls != rs,
                    "<" => string.CompareOrdinal(ls, rs) < 0,
                    "<=" => string.CompareOrdinal(ls, rs) <= 0,
                    ">" => string.CompareOrdinal(ls, rs) > 0,
                    ">=" => string.CompareOrdinal(ls, rs) >= 0,
                    _ => false,
                };

            return op switch { "==" => Equals(left, right), "!=" => !Equals(left, right), _ => false };
        }

        private static double? ToDouble(object? v) =>
            v switch
            {
                double d => d,
                int i => i,
                long l => l,
                _ => null,
            };
    }

    // ─── Data types: comparable values ───────────────────────────────────────

    private interface IComparableValue
    {
        object? Evaluate(JsonElement current, FilterContext ctx);
    }

    private sealed class NullLiteral : IComparableValue
    {
        public static readonly NullLiteral Instance = new();
        public object? Evaluate(JsonElement current, FilterContext ctx) => null;
    }

    private sealed class BoolLiteral(bool value) : IComparableValue
    {
        public static readonly BoolLiteral True = new(true);
        public static readonly BoolLiteral False = new(false);
        public object? Evaluate(JsonElement current, FilterContext ctx) => value;
    }

    private sealed class StringLiteral(string value) : IComparableValue
    {
        public object? Evaluate(JsonElement current, FilterContext ctx) => value;
    }

    private sealed class NumberLiteral(double value) : IComparableValue
    {
        public object? Evaluate(JsonElement current, FilterContext ctx) => value;
    }

    private sealed class PathComparable(bool isRelative, IReadOnlyList<Segment> segments)
        : IComparableValue
    {
        public IReadOnlyList<JsonElement> GetNodes(JsonElement current, FilterContext ctx)
        {
            var startNode = isRelative ? current : ctx.Root;
            return EvaluateSegments([startNode], ctx.Root, segments);
        }

        public object? Evaluate(JsonElement current, FilterContext ctx)
        {
            var nodes = GetNodes(current, ctx);
            if (nodes.Count == 0) return null;
            // Non-singular path used in comparison returns null per RFC 9535
            if (nodes.Count > 1) return null;
            return JsonElementToObject(nodes[0]);
        }
    }

    private sealed class FunctionCallComparable(
        string name, IReadOnlyList<IComparableValue> args) : IComparableValue
    {
        public object? Evaluate(JsonElement current, FilterContext ctx) =>
            name switch
            {
                "length" => EvaluateLength(current, ctx),
                "count" => EvaluateCount(current, ctx),
                "match" => EvaluateMatch(current, ctx),
                "search" => EvaluateSearch(current, ctx),
                "value" => EvaluateValue(current, ctx),
                _ => null,
            };

        private object? EvaluateLength(JsonElement current, FilterContext ctx)
        {
            if (args.Count != 1) return null;
            var val = args[0].Evaluate(current, ctx);
            return val switch
            {
                string s => (double)s.Length,
                JsonElement e when e.ValueKind == JsonValueKind.String =>
                    (double)(e.GetString()?.Length ?? 0),
                JsonElement e when e.ValueKind == JsonValueKind.Array =>
                    (double)e.GetArrayLength(),
                JsonElement e when e.ValueKind == JsonValueKind.Object =>
                    (double)CountObjectProperties(e),
                _ => null,
            };
        }

        private static int CountObjectProperties(JsonElement element)
        {
            var count = 0;
            foreach (var _ in element.EnumerateObject()) count++;
            return count;
        }

        private object? EvaluateCount(JsonElement current, FilterContext ctx)
        {
            if (args.Count != 1) return null;
            if (args[0] is PathComparable pc)
                return (double)pc.GetNodes(current, ctx).Count;
            return null;
        }

        private object? EvaluateMatch(JsonElement current, FilterContext ctx)
        {
            if (args.Count != 2) return null;
            var val = args[0].Evaluate(current, ctx) as string;
            var pattern = args[1].Evaluate(current, ctx) as string;
            if (val is null || pattern is null) return false;
            try { return Regex.IsMatch(val, $"^(?:{pattern})$", RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch { return false; }
        }

        private object? EvaluateSearch(JsonElement current, FilterContext ctx)
        {
            if (args.Count != 2) return null;
            var val = args[0].Evaluate(current, ctx) as string;
            var pattern = args[1].Evaluate(current, ctx) as string;
            if (val is null || pattern is null) return false;
            try { return Regex.IsMatch(val, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch { return false; }
        }

        private object? EvaluateValue(JsonElement current, FilterContext ctx)
        {
            if (args.Count != 1) return null;
            if (args[0] is PathComparable pc)
            {
                var nodes = pc.GetNodes(current, ctx);
                return nodes.Count == 1 ? JsonElementToObject(nodes[0]) : null;
            }
            return null;
        }
    }
}
