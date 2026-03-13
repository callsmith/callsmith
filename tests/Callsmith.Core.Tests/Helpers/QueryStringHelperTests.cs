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
        result["foo"].Should().Be("bar");
    }

    [Fact]
    public void ParseQueryParams_UrlWithMultipleParams_ReturnsAllEntries()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com?a=1&b=2&c=3");
        result.Should().HaveCount(3);
        result["a"].Should().Be("1");
        result["b"].Should().Be("2");
        result["c"].Should().Be("3");
    }

    [Fact]
    public void ParseQueryParams_EncodedChars_DecodesKeyAndValue()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com?search=hello%20world&tag=c%23");
        result["search"].Should().Be("hello world");
        result["tag"].Should().Be("c#");
    }

    [Fact]
    public void ParseQueryParams_ParamWithNoValue_ReturnsEmptyString()
    {
        var result = QueryStringHelper.ParseQueryParams("https://api.example.com?flag");
        result["flag"].Should().BeEmpty();
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
        reparsed.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["q"] = "test",
            ["limit"] = "10",
            ["offset"] = "0",
        });
    }
}
