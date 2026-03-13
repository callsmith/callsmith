using Callsmith.Core.Helpers;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

public sealed class PathTemplateHelperTests
{
    [Fact]
    public void ExtractPathParamNames_EmptyInput_ReturnsEmpty()
    {
        PathTemplateHelper.ExtractPathParamNames(string.Empty).Should().BeEmpty();
        PathTemplateHelper.ExtractPathParamNames("  ").Should().BeEmpty();
    }

    [Fact]
    public void ExtractPathParamNames_FindsDistinctNamesInOrder()
    {
        var names = PathTemplateHelper.ExtractPathParamNames(
            "https://api.example.com/users/{id}/orders/{orderId}/{id}");

        names.Should().ContainInOrder("id", "orderId");
    }

    [Fact]
    public void ExtractPathParamNames_IgnoresQueryAndFragment()
    {
        var names = PathTemplateHelper.ExtractPathParamNames(
            "https://api.example.com/users/{id}?x={query}#frag/{part}");

        names.Should().Equal("id");
    }

    [Fact]
    public void ExtractPathParamNames_DoesNotMatchDoubleBraceEnvTokens()
    {
        var names = PathTemplateHelper.ExtractPathParamNames(
            "https://api.example.com/{{tenant}}/users/{id}");

        names.Should().Equal("id");
    }

    [Fact]
    public void ApplyPathParams_ReplacesKnownPlaceholders()
    {
        var result = PathTemplateHelper.ApplyPathParams(
            "https://api.example.com/users/{id}/orders/{orderId}",
            new Dictionary<string, string>
            {
                ["id"] = "42",
                ["orderId"] = "abc",
            });

        result.Should().Be("https://api.example.com/users/42/orders/abc");
    }

    [Fact]
    public void ApplyPathParams_LeavesUnknownPlaceholderUnchanged()
    {
        var result = PathTemplateHelper.ApplyPathParams(
            "https://api.example.com/users/{id}/orders/{orderId}",
            new Dictionary<string, string> { ["id"] = "42" });

        result.Should().Be("https://api.example.com/users/42/orders/{orderId}");
    }

    [Fact]
    public void ApplyPathParams_EncodesReplacementValues()
    {
        var result = PathTemplateHelper.ApplyPathParams(
            "https://api.example.com/users/{id}",
            new Dictionary<string, string> { ["id"] = "a b/c" });

        result.Should().Be("https://api.example.com/users/a%20b%2Fc");
    }

    [Fact]
    public void ApplyPathParams_PreservesQueryAndFragment()
    {
        var result = PathTemplateHelper.ApplyPathParams(
            "https://api.example.com/users/{id}?x=1#details",
            new Dictionary<string, string> { ["id"] = "42" });

        result.Should().Be("https://api.example.com/users/42?x=1#details");
    }
}
