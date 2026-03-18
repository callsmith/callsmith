using Callsmith.Core.Helpers;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

public sealed class QueryStringHelperTests
{
    // -------------------------------------------------------------------------
    // ParseQueryParams
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseQueryParams_NullOrEmptyUrl_ReturnsEmpty()
    {
        QueryStringHelper.ParseQueryParams(string.Empty).Should().BeEmpty();
        QueryStringHelper.ParseQueryParams("not-a-url").Should().BeEmpty();
    }

    [Fact]
    public void ParseQueryParams_UrlWithNoQuery_ReturnsEmpty()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com/users");
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseQueryParams_UrlWithSingleParam_ReturnsSingleEntry()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com?foo=bar");
        result.Should().HaveCount(1);
        result[0].Key.Should().Be("foo");
        result[0].Value.Should().Be("bar");
    }

    [Fact]
    public void ParseQueryParams_UrlWithMultipleParams_ReturnsAllEntries()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com?a=1&b=2&c=3");
        result.Should().HaveCount(3);
        result[0].Key.Should().Be("a"); result[0].Value.Should().Be("1");
        result[1].Key.Should().Be("b"); result[1].Value.Should().Be("2");
        result[2].Key.Should().Be("c"); result[2].Value.Should().Be("3");
    }

    [Fact]
    public void ParseQueryParams_EncodedChars_DecodesKeyAndValue()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com?search=hello%20world&tag=c%23");
        result.First(p => p.Key == "search").Value.Should().Be("hello world");
        result.First(p => p.Key == "tag").Value.Should().Be("c#");
    }

    [Fact]
    public void ParseQueryParams_ParamWithNoValue_ReturnsEmptyString()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com?flag");
        result.First(p => p.Key == "flag").Value.Should().BeEmpty();
    }

    [Fact]
    public void ParseQueryParams_DuplicateKeys_PreservesAllOccurrences()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com?role=admin&role=user&role=viewer");
        result.Should().HaveCount(3);
        result.Select(p => p.Value).Should().BeEquivalentTo(["admin", "user", "viewer"]);
    }

    // -------------------------------------------------------------------------
    // ApplyQueryParams
    // -------------------------------------------------------------------------

    [Fact]
    public void ApplyQueryParams_EmptyList_StripsExistingQuery()
    {
        var result = QueryStringHelper.ApplyQueryParams(
            "https://api.example.com?old=value",
            []);
        result.Should().Be("https://api.example.com");
    }

    [Fact]
    public void ApplyQueryParams_EmptyListNoQuery_ReturnsUrlUnchanged()
    {
        var result = QueryStringHelper.ApplyQueryParams("https://api.example.com/users", []);
        result.Should().Be("https://api.example.com/users");
    }

    [Fact]
    public void ApplyQueryParams_WithParams_BuildsQueryString()
    {
        var pairs = new Dictionary<string, string> { ["foo"] = "bar", ["baz"] = "qux" };
        var result = QueryStringHelper.ApplyQueryParams("https://api.example.com", pairs);
        result.Should().Be("https://api.example.com?foo=bar&baz=qux");
    }

    [Fact]
    public void ApplyQueryParams_ReplacesExistingQueryString()
    {
        var pairs = new Dictionary<string, string> { ["newKey"] = "newVal" };
        var result = QueryStringHelper.ApplyQueryParams(
            "https://api.example.com?oldKey=oldVal",
            pairs);
        result.Should().Be("https://api.example.com?newKey=newVal");
    }

    [Fact]
    public void ApplyQueryParams_EncodesSpecialCharsInKeysAndValues()
    {
        var pairs = new Dictionary<string, string> { ["q"] = "hello world", ["tag"] = "c#" };
        var result = QueryStringHelper.ApplyQueryParams("https://api.example.com", pairs);
        result.Should().Be("https://api.example.com?q=hello%20world&tag=c%23");
    }

    [Fact]
    public void RoundTrip_ParseThenApply_PreservesParams()
    {
        const string url = "https://api.example.com/search?q=test&limit=10&offset=0";
        var parsed = QueryStringHelper.ParseQueryParams(url);
        var rebuilt = QueryStringHelper.ApplyQueryParams("https://api.example.com/search", parsed);
        // Re-parse to compare regardless of order
        var reparsed = QueryStringHelper.ParseQueryParams(rebuilt);
        reparsed.Select(p => p.Key).Should().BeEquivalentTo(["q", "limit", "offset"]);
        reparsed.First(p => p.Key == "q").Value.Should().Be("test");
        reparsed.First(p => p.Key == "limit").Value.Should().Be("10");
        reparsed.First(p => p.Key == "offset").Value.Should().Be("0");
    }
}
