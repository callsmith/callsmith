using System.Text.Json;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Services;
using FluentAssertions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="JsonPathService"/> covering RFC 9535 compliance.
/// </summary>
public sealed class JsonPathServiceTests
{
    private readonly IJsonPathService _sut = new JsonPathService();

    // ─── Helper ───────────────────────────────────────────────────────────────

    private IReadOnlyList<JsonElement> Query(string json, string path)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        return _sut.Query(root, path);
    }

    private string? QuerySingle(string json, string path)
    {
        var results = Query(json, path);
        if (results.Count == 0) return null;
        var el = results[0];
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => el.GetRawText(),
        };
    }

    // ─── Root ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Query_Root_ReturnsSingleRootElement()
    {
        var results = Query("""{"a":1}""", "$");
        results.Should().HaveCount(1);
        results[0].ValueKind.Should().Be(JsonValueKind.Object);
    }

    // ─── Name selectors ───────────────────────────────────────────────────────

    [Fact]
    public void Query_DotNotation_ExtractsTopLevelProperty()
    {
        QuerySingle("""{"name":"Alice"}""", "$.name").Should().Be("Alice");
    }

    [Fact]
    public void Query_DotNotation_NestedPath()
    {
        QuerySingle("""{"a":{"b":{"c":42}}}""", "$.a.b.c").Should().Be("42");
    }

    [Fact]
    public void Query_BracketQuotedName_SingleQuotes()
    {
        QuerySingle("""{"name":"Bob"}""", "$['name']").Should().Be("Bob");
    }

    [Fact]
    public void Query_BracketQuotedName_DoubleQuotes()
    {
        QuerySingle("""{"name":"Bob"}""", """$["name"]""").Should().Be("Bob");
    }

    [Fact]
    public void Query_BracketQuotedName_WithSpecialCharacters()
    {
        var json = """{"key.with.dot":"value"}""";
        QuerySingle(json, "$['key.with.dot']").Should().Be("value");
    }

    [Fact]
    public void Query_BracketQuotedName_WithEscapeSequences()
    {
        var json = """{"line\nbreak":"yes"}""";
        QuerySingle(json, """$['line\nbreak']""").Should().Be("yes");
    }

    [Fact]
    public void Query_NameNotFound_ReturnsEmpty()
    {
        Query("""{"a":1}""", "$.b").Should().BeEmpty();
    }

    [Fact]
    public void Query_NameOnNonObject_ReturnsEmpty()
    {
        Query("""[1,2,3]""", "$.name").Should().BeEmpty();
    }

    // ─── Index selectors ──────────────────────────────────────────────────────

    [Fact]
    public void Query_IndexZero_ReturnsFirstElement()
    {
        QuerySingle("""[10,20,30]""", "$[0]").Should().Be("10");
    }

    [Fact]
    public void Query_IndexLast_ReturnsLastElement()
    {
        QuerySingle("""[10,20,30]""", "$[2]").Should().Be("30");
    }

    [Fact]
    public void Query_NegativeIndex_CountsFromEnd()
    {
        QuerySingle("""[10,20,30]""", "$[-1]").Should().Be("30");
        QuerySingle("""[10,20,30]""", "$[-2]").Should().Be("20");
        QuerySingle("""[10,20,30]""", "$[-3]").Should().Be("10");
    }

    [Fact]
    public void Query_IndexOutOfRange_ReturnsEmpty()
    {
        Query("""[1,2,3]""", "$[5]").Should().BeEmpty();
        Query("""[1,2,3]""", "$[-5]").Should().BeEmpty();
    }

    [Fact]
    public void Query_IndexOnNonArray_ReturnsEmpty()
    {
        Query("""{"a":1}""", "$[0]").Should().BeEmpty();
    }

    // ─── Wildcard selector ────────────────────────────────────────────────────

    [Fact]
    public void Query_WildcardDotNotation_ReturnsAllObjectValues()
    {
        var results = Query("""{"a":1,"b":2,"c":3}""", "$.*");
        results.Should().HaveCount(3);
    }

    [Fact]
    public void Query_WildcardBracketNotation_ReturnsAllArrayElements()
    {
        var results = Query("""[10,20,30]""", "$[*]");
        results.Should().HaveCount(3);
        results.Select(e => e.GetInt32()).Should().Equal(10, 20, 30);
    }

    [Fact]
    public void Query_WildcardOnEmptyArray_ReturnsEmpty()
    {
        Query("""[]""", "$[*]").Should().BeEmpty();
    }

    [Fact]
    public void Query_WildcardOnEmptyObject_ReturnsEmpty()
    {
        Query("""{}""", "$.*").Should().BeEmpty();
    }

    [Fact]
    public void Query_WildcardChained_ExtractsNestedValues()
    {
        var json = """
            {
              "books": [
                {"title": "A"},
                {"title": "B"}
              ]
            }
            """;
        var results = Query(json, "$.books[*].title");
        results.Should().HaveCount(2);
        results.Select(e => e.GetString()).Should().Equal("A", "B");
    }

    // ─── Slice selectors ──────────────────────────────────────────────────────

    [Fact]
    public void Query_Slice_BasicRange()
    {
        var results = Query("""[0,1,2,3,4]""", "$[1:3]");
        results.Should().HaveCount(2);
        results.Select(e => e.GetInt32()).Should().Equal(1, 2);
    }

    [Fact]
    public void Query_Slice_FromStartToIndex()
    {
        var results = Query("""[0,1,2,3,4]""", "$[:3]");
        results.Should().HaveCount(3);
        results.Select(e => e.GetInt32()).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Query_Slice_FromIndexToEnd()
    {
        var results = Query("""[0,1,2,3,4]""", "$[3:]");
        results.Should().HaveCount(2);
        results.Select(e => e.GetInt32()).Should().Equal(3, 4);
    }

    [Fact]
    public void Query_Slice_WithStep()
    {
        var results = Query("""[0,1,2,3,4,5,6]""", "$[::2]");
        results.Select(e => e.GetInt32()).Should().Equal(0, 2, 4, 6);
    }

    [Fact]
    public void Query_Slice_NegativeStep_ReversesArray()
    {
        var results = Query("""[0,1,2,3,4]""", "$[::-1]");
        results.Select(e => e.GetInt32()).Should().Equal(4, 3, 2, 1, 0);
    }

    [Fact]
    public void Query_Slice_NegativeStep_WithBounds()
    {
        var results = Query("""[0,1,2,3,4]""", "$[4:1:-1]");
        results.Select(e => e.GetInt32()).Should().Equal(4, 3, 2);
    }

    [Fact]
    public void Query_Slice_NegativeIndex()
    {
        var results = Query("""[0,1,2,3,4]""", "$[-2:]");
        results.Select(e => e.GetInt32()).Should().Equal(3, 4);
    }

    [Fact]
    public void Query_Slice_ZeroStep_ReturnsEmpty()
    {
        Query("""[0,1,2,3]""", "$[0:3:0]").Should().BeEmpty();
    }

    [Fact]
    public void Query_Slice_OnNonArray_ReturnsEmpty()
    {
        Query("""{"a":1}""", "$[1:3]").Should().BeEmpty();
    }

    // ─── Descendant segments ──────────────────────────────────────────────────

    [Fact]
    public void Query_Descendant_FindsAllMatchingProperties()
    {
        var json = """
            {
              "store": {
                "book": [
                  {"author": "Evelyn"},
                  {"author": "James"}
                ],
                "author": "unknown"
              }
            }
            """;
        var results = Query(json, "$..author");
        results.Should().HaveCount(3);
    }

    [Fact]
    public void Query_DescendantWildcard_ReturnsAllNodes()
    {
        var json = """{"a":{"b":1,"c":2},"d":3}""";
        var results = Query(json, "$..*");
        results.Count.Should().BeGreaterThan(3);
    }

    [Fact]
    public void Query_Descendant_DoesNotMatchRoot()
    {
        // $..name should not include the root when root has no 'name'
        var json = """{"data":{"name":"Alice"}}""";
        var results = Query(json, "$..name");
        results.Should().HaveCount(1);
        results[0].GetString().Should().Be("Alice");
    }

    [Fact]
    public void Query_DescendantIndex_FindsNestedArrayElements()
    {
        var json = """{"a":[[1,2],[3,4]]}""";
        var results = Query(json, "$..a[0]");
        results.Should().HaveCount(1);
    }

    // ─── Multiple selectors in brackets ──────────────────────────────────────

    [Fact]
    public void Query_MultipleIndexSelectors_ReturnsAllSelectedElements()
    {
        var results = Query("""[10,20,30,40,50]""", "$[0,2,4]");
        results.Select(e => e.GetInt32()).Should().Equal(10, 30, 50);
    }

    [Fact]
    public void Query_MultipleNameSelectors_ReturnsAllSelectedProperties()
    {
        var results = Query("""{"a":1,"b":2,"c":3}""", "$['a','c']");
        results.Should().HaveCount(2);
        results.Select(e => e.GetInt32()).Should().Equal(1, 3);
    }

    [Fact]
    public void Query_MixedSelectors_WildcardAndIndex()
    {
        // $[*,0] on [1,2,3] → all elements + first element = [1,2,3,1]
        var results = Query("""[1,2,3]""", "$[*,0]");
        results.Should().HaveCount(4);
    }

    // ─── Filter selector ──────────────────────────────────────────────────────

    [Fact]
    public void Query_Filter_ExistenceTest()
    {
        var json = """
            [
              {"name": "Alice", "age": 30},
              {"name": "Bob"},
              {"name": "Carol", "age": 25}
            ]
            """;
        var results = Query(json, "$[?@.age]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_ComparisonGreaterThan()
    {
        var json = """
            [
              {"name": "Alice", "age": 30},
              {"name": "Bob", "age": 18},
              {"name": "Carol", "age": 25}
            ]
            """;
        var results = Query(json, "$[?@.age > 20]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_ComparisonEquals()
    {
        var json = """
            [{"status":"active"},{"status":"inactive"},{"status":"active"}]
            """;
        var results = Query(json, "$[?@.status == 'active']");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_ComparisonNotEquals()
    {
        var json = """[{"v":1},{"v":2},{"v":1}]""";
        var results = Query(json, "$[?@.v != 1]");
        results.Should().HaveCount(1);
    }

    [Fact]
    public void Query_Filter_ComparisonLessOrEqual()
    {
        var json = """[{"n":1},{"n":2},{"n":3}]""";
        var results = Query(json, "$[?@.n <= 2]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_LogicalAnd()
    {
        var json = """
            [
              {"age": 30, "active": true},
              {"age": 30, "active": false},
              {"age": 15, "active": true}
            ]
            """;
        var results = Query(json, "$[?@.age >= 18 && @.active == true]");
        results.Should().HaveCount(1);
    }

    [Fact]
    public void Query_Filter_LogicalOr()
    {
        var json = """[{"n":1},{"n":5},{"n":10}]""";
        var results = Query(json, "$[?@.n == 1 || @.n == 10]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_LogicalNot_ExistenceTest()
    {
        var json = """
            [{"name":"Alice","age":30},{"name":"Bob"}]
            """;
        var results = Query(json, "$[?!@.age]");
        results.Should().HaveCount(1);
        results[0].GetProperty("name").GetString().Should().Be("Bob");
    }

    [Fact]
    public void Query_Filter_ParenthesisGrouping()
    {
        var json = """[{"n":1},{"n":2},{"n":3},{"n":4}]""";
        var results = Query(json, "$[?(@.n == 1 || @.n == 2) && @.n != 1]");
        results.Should().HaveCount(1);
        results[0].GetProperty("n").GetInt32().Should().Be(2);
    }

    [Fact]
    public void Query_Filter_AbsolutePathReference()
    {
        var json = """
            {
              "threshold": 25,
              "items": [
                {"val": 10},
                {"val": 30},
                {"val": 20}
              ]
            }
            """;
        var results = Query(json, "$.items[?@.val > $.threshold]");
        results.Should().HaveCount(1);
        results[0].GetProperty("val").GetInt32().Should().Be(30);
    }

    [Fact]
    public void Query_Filter_StringComparison()
    {
        var json = """[{"n":"apple"},{"n":"banana"},{"n":"cherry"}]""";
        var results = Query(json, "$[?@.n == 'banana']");
        results.Should().HaveCount(1);
    }

    [Fact]
    public void Query_Filter_BooleanLiteralComparison()
    {
        var json = """[{"active":true},{"active":false},{"active":true}]""";
        var results = Query(json, "$[?@.active == true]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_NullComparison()
    {
        var json = """[{"v":null},{"v":1},{"v":null}]""";
        var results = Query(json, "$[?@.v == null]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_NestedPath()
    {
        var json = """
            [
              {"user": {"role": "admin"}},
              {"user": {"role": "guest"}},
              {"user": {"role": "admin"}}
            ]
            """;
        var results = Query(json, "$[?@.user.role == 'admin']");
        results.Should().HaveCount(2);
    }

    // ─── RFC 9535 built-in functions ─────────────────────────────────────────

    [Fact]
    public void Query_Filter_LengthFunction_String()
    {
        var json = """[{"s":"hello"},{"s":"hi"},{"s":"world"}]""";
        var results = Query(json, "$[?length(@.s) > 3]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_LengthFunction_Array()
    {
        var json = """[{"items":[1,2,3]},{"items":[1]},{"items":[1,2]}]""";
        var results = Query(json, "$[?length(@.items) >= 2]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_CountFunction()
    {
        var json = """
            {
              "items": [
                {"tags":["a","b","c"]},
                {"tags":["x"]},
                {"tags":["p","q"]}
              ]
            }
            """;
        // count(@.tags[*]) counts the nodelist of array elements
        var results = Query(json, "$.items[?count(@.tags[*]) > 1]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_MatchFunction_FullMatch()
    {
        var json = """[{"name":"Alice"},{"name":"Bob123"},{"name":"Carol"}]""";
        var results = Query(json, "$[?match(@.name, '[A-Za-z]+')]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_SearchFunction_PartialMatch()
    {
        var json = """[{"email":"test@example.com"},{"email":"other@test.org"},{"email":"plain"}]""";
        var results = Query(json, "$[?search(@.email, '@')]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_ValueFunction()
    {
        // value() returns the scalar value from a singleton nodelist
        var json = """
            [
              {"rating":5},
              {"rating":3},
              {"rating":5}
            ]
            """;
        var results = Query(json, "$[?value(@.rating) == 5]");
        results.Should().HaveCount(2);
    }

    // ─── Bracket notation mixed with dot notation ─────────────────────────────

    [Fact]
    public void Query_MixedNotation_BracketThenDot()
    {
        var json = """{"store":{"book":[{"title":"T1"},{"title":"T2"}]}}""";
        QuerySingle(json, "$.store['book'][0].title").Should().Be("T1");
    }

    [Fact]
    public void Query_MixedNotation_DotThenBracket()
    {
        var json = """{"a":{"b":[10,20,30]}}""";
        QuerySingle(json, "$.a.b[2]").Should().Be("30");
    }

    // ─── Descendant with filter ────────────────────────────────────────────────

    [Fact]
    public void Query_DescendantWithFilter_FindsNestedMatches()
    {
        var json = """
            {
              "root": {
                "items": [{"price": 5}, {"price": 15}],
                "nested": {
                  "items": [{"price": 8}, {"price": 25}]
                }
              }
            }
            """;
        var results = Query(json, "$..items[?@.price > 10]");
        results.Should().HaveCount(2);
    }

    // ─── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void TryValidate_ValidExpression_ReturnsTrue()
    {
        _sut.TryValidate("$.store.book[0].title", out var error).Should().BeTrue();
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryValidate_MissingDollar_ReturnsFalse()
    {
        _sut.TryValidate("store.book", out var error).Should().BeFalse();
        error.Should().Contain("must start with '$'");
    }

    [Fact]
    public void TryValidate_EmptyString_ReturnsFalse()
    {
        _sut.TryValidate("", out var error).Should().BeFalse();
        error.Should().Contain("empty");
    }

    [Fact]
    public void TryValidate_MissingClosingBracket_ReturnsFalse()
    {
        _sut.TryValidate("$[0", out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryValidate_EmptyBracket_ReturnsFalse()
    {
        _sut.TryValidate("$[]", out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryValidate_UnterminatedString_ReturnsFalse()
    {
        _sut.TryValidate("$['unterminated", out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryValidate_LeadingZeroInIndex_ReturnsFalse()
    {
        _sut.TryValidate("$[01]", out var error).Should().BeFalse();
        error.Should().Contain("Leading zeros");
    }

    [Fact]
    public void TryValidate_ValidFilter_ReturnsTrue()
    {
        _sut.TryValidate("$[?@.age > 18]", out var error).Should().BeTrue();
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryValidate_InvalidFilter_MissingClosingParen_ReturnsFalse()
    {
        _sut.TryValidate("$[?(@.age > 18]", out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    // ─── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Query_EmptyResult_ReturnsEmptyList()
    {
        Query("""{"a":1}""", "$.b.c.d").Should().BeEmpty();
    }

    [Fact]
    public void Query_InvalidExpression_ReturnsEmptyList()
    {
        Query("""{"a":1}""", "not-valid").Should().BeEmpty();
    }

    [Fact]
    public void Query_ArrayOfScalars_WildcardAndIndex()
    {
        var results = Query("""["a","b","c"]""", "$[*]");
        results.Select(e => e.GetString()).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Query_DeepNesting_PathTraversal()
    {
        var json = """{"a":{"b":{"c":{"d":{"e":99}}}}}""";
        QuerySingle(json, "$.a.b.c.d.e").Should().Be("99");
    }

    [Fact]
    public void Query_NullJsonValue_ReturnsNullElement()
    {
        var results = Query("""{"v":null}""", "$.v");
        results.Should().HaveCount(1);
        results[0].ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Query_BooleanJsonValues_ReturnedCorrectly()
    {
        QuerySingle("""{"flag":true}""", "$.flag").Should().Be("true");
        QuerySingle("""{"flag":false}""", "$.flag").Should().Be("false");
    }

    [Fact]
    public void Query_Filter_ComparisonWithNegativeNumber()
    {
        var json = """[{"temp":-5},{"temp":10},{"temp":-20}]""";
        var results = Query(json, "$[?@.temp < 0]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_ComparisonWithDecimalNumber()
    {
        var json = """[{"price":9.99},{"price":15.50},{"price":4.99}]""";
        var results = Query(json, "$[?@.price < 10.0]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Slice_FullCopy()
    {
        var results = Query("""[0,1,2,3,4]""", "$[:]");
        results.Select(e => e.GetInt32()).Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public void Query_DescendantBracketNotation_WorksLikeDot()
    {
        var json = """{"a":{"b":1},"c":{"b":2}}""";
        var results = Query(json, "$..[\"b\"]");
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Query_Filter_ExistenceTestOnObject()
    {
        // Filter on an object iterates over its member values; those with a 'flag' property are selected
        var json = """{"a":{"flag":1},"b":{},"c":{"flag":2}}""";
        var results = Query(json, "$[?@.flag]");
        results.Should().HaveCount(2);
    }
}
