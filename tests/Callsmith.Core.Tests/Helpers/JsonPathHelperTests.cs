using Callsmith.Core.Helpers;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

/// <summary>Tests for <see cref="JsonPathHelper"/>.</summary>
public sealed class JsonPathHelperTests
{
    [Theory]
    [InlineData("""{"token":"abc123"}""", "$.token", "abc123")]
    [InlineData("""{"token":"abc123"}""", "$", """{"token":"abc123"}""")]
    [InlineData("""{"data":{"access_token":"xyz"}}""", "$.data.access_token", "xyz")]
    [InlineData("""{"results":[{"value":"first"},{"value":"second"}]}""", "$.results[0].value", "first")]
    [InlineData("""{"results":[{"value":"first"},{"value":"second"}]}""", "$.results[1].value", "second")]
    [InlineData("""{"count":42}""", "$.count", "42")]
    [InlineData("""{"flag":true}""", "$.flag", "true")]
    [InlineData("""{"flag":false}""", "$.flag", "false")]
    public void Extract_ReturnsExpectedValue(string json, string path, string expected)
    {
        var result = JsonPathHelper.Extract(json, path);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"token":"abc"}""", "$.missing", null)]
    [InlineData("""{"a":{"b":"v"}}""", "$.a.c", null)]
    [InlineData("null", "$.token", null)]
    [InlineData("{}", "$.token", null)]
    public void Extract_ReturnsNullWhenPathNotFound(string json, string path, string? expected)
    {
        var result = JsonPathHelper.Extract(json, path);
        result.Should().Be(expected);
    }

    [Fact]
    public void Extract_ReturnsNullForInvalidJson()
    {
        var result = JsonPathHelper.Extract("not-json", "$.token");
        result.Should().BeNull();
    }

    [Fact]
    public void Extract_ReturnsNullForNullOrEmptyInputs()
    {
        JsonPathHelper.Extract("", "$.token").Should().BeNull();
        JsonPathHelper.Extract("""{"t":"v"}""", "").Should().BeNull();
    }

    [Fact]
    public void Extract_ArrayIndex_ReturnsCorrectElement()
    {
        var json = """[{"name":"first"},{"name":"second"}]""";
        JsonPathHelper.Extract(json, "$[0].name").Should().Be("first");
        JsonPathHelper.Extract(json, "$[1].name").Should().Be("second");
    }
}
