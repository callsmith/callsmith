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

    [Fact]
    public void ApplyPathParams_ReplacesBrunoColonPlaceholdersToo()
    {
        var result = PathTemplateHelper.ApplyPathParams(
            "https://api.example.com/jokes/:kind",
            new Dictionary<string, string> { ["kind"] = "random" });

        result.Should().Be("https://api.example.com/jokes/random");
    }

    // ── ExtractPathParamNamesBoth ─────────────────────────────────────────────

    [Fact]
    public void ExtractPathParamNamesBoth_EmptyInput_ReturnsEmpty()
    {
        PathTemplateHelper.ExtractPathParamNamesBoth(string.Empty).Should().BeEmpty();
        PathTemplateHelper.ExtractPathParamNamesBoth("  ").Should().BeEmpty();
    }

    [Fact]
    public void ExtractPathParamNamesBoth_PureBraceUrl_FindsBraceParams()
    {
        var names = PathTemplateHelper.ExtractPathParamNamesBoth(
            "https://api.example.com/users/{id}/orders/{orderId}");

        names.Should().ContainInOrder("id", "orderId");
    }

    [Fact]
    public void ExtractPathParamNamesBoth_PureColonUrl_FindsColonParams()
    {
        var names = PathTemplateHelper.ExtractPathParamNamesBoth(
            "https://api.example.com/users/:id/orders/:orderId");

        names.Should().ContainInOrder("id", "orderId");
    }

    [Fact]
    public void ExtractPathParamNamesBoth_MixedUrl_FindsBothInOrder()
    {
        // brace first, then colon
        var names = PathTemplateHelper.ExtractPathParamNamesBoth(
            "https://api.example.com/users/{id}/orders/:orderId");

        names.Should().ContainInOrder("id", "orderId");
    }

    [Fact]
    public void ExtractPathParamNamesBoth_MixedUrl_ColonFirst_FindsBothInOrder()
    {
        // colon first, then brace
        var names = PathTemplateHelper.ExtractPathParamNamesBoth(
            "https://api.example.com/users/:id/orders/{orderId}");

        names.Should().ContainInOrder("id", "orderId");
    }

    [Fact]
    public void ExtractPathParamNamesBoth_DeduplicatesAcrossSyntaxes()
    {
        // same name appears as both forms (unusual but should deduplicate)
        var names = PathTemplateHelper.ExtractPathParamNamesBoth(
            "https://api.example.com/{id}/:id");

        names.Should().Equal("id");
    }

    [Fact]
    public void ExtractPathParamNamesBoth_DoesNotMatchDoubleBraceEnvTokens()
    {
        var names = PathTemplateHelper.ExtractPathParamNamesBoth(
            "https://api.example.com/{{tenant}}/users/{id}");

        names.Should().Equal("id");
    }

    [Fact]
    public void ExtractPathParamNamesBoth_IgnoresQueryAndFragment()
    {
        var names = PathTemplateHelper.ExtractPathParamNamesBoth(
            "https://api.example.com/users/{id}?x=:query#frag/:part");

        names.Should().Equal("id");
    }

    // ── ApplyPathParams colon / {{token}} parity ─────────────────────────────

    [Fact]
    public void ApplyPathParams_ColonPlaceholder_WithDynamicTokenValue_PreservesTokenVerbatim()
    {
        // Parity with brace behaviour: a value containing {{...}} must not be URL-encoded.
        var result = PathTemplateHelper.ApplyPathParams(
            "https://api.example.com/users/:id",
            new Dictionary<string, string> { ["id"] = "{{userId}}" });

        result.Should().Be("https://api.example.com/users/{{userId}}");
    }

    [Fact]
    public void ApplyPathParams_BraceAndColonParityForDynamicTokens()
    {
        // Both placeholder forms must produce identical output for a {{token}} value.
        var values = new Dictionary<string, string> { ["id"] = "{{userId}}" };

        var braceResult = PathTemplateHelper.ApplyPathParams(
            "https://api.example.com/users/{id}", values);

        var colonResult = PathTemplateHelper.ApplyPathParams(
            "https://api.example.com/users/:id", values);

        braceResult.Should().Be("https://api.example.com/users/{{userId}}");
        colonResult.Should().Be(braceResult);
    }

    // ── RenamePathParam ───────────────────────────────────────────────────────

    [Fact]
    public void RenamePathParam_BraceForm_RenamesBrace()
    {
        var result = PathTemplateHelper.RenamePathParam(
            "https://api.example.com/users/{id}", "id", "userId");

        result.Should().Be("https://api.example.com/users/{userId}");
    }

    [Fact]
    public void RenamePathParam_ColonForm_RenamesColon()
    {
        var result = PathTemplateHelper.RenamePathParam(
            "https://api.example.com/users/:id", "id", "userId");

        result.Should().Be("https://api.example.com/users/:userId");
    }

    [Fact]
    public void RenamePathParam_MixedUrl_RenamesBothForms()
    {
        // A single param name that appears in brace form in one segment and colon form
        // in another is unlikely but must be handled consistently.
        var result = PathTemplateHelper.RenamePathParam(
            "https://api.example.com/{id}/:id", "id", "userId");

        result.Should().Be("https://api.example.com/{userId}/:userId");
    }

    [Fact]
    public void RenamePathParam_PreservesQueryString()
    {
        var result = PathTemplateHelper.RenamePathParam(
            "https://api.example.com/users/{id}?x=1", "id", "userId");

        result.Should().Be("https://api.example.com/users/{userId}?x=1");
    }

    [Fact]
    public void RenamePathParam_LeavesOtherParamsUntouched()
    {
        var result = PathTemplateHelper.RenamePathParam(
            "https://api.example.com/users/{id}/orders/:orderId", "id", "userId");

        result.Should().Be("https://api.example.com/users/{userId}/orders/:orderId");
    }

    [Fact]
    public void RenamePathParam_BraceForm_DoesNotRenameDoubleBraceEnvTokens()
    {
        var result = PathTemplateHelper.RenamePathParam(
            "https://api.example.com/{{tenant}}/users/{tenant}", "tenant", "accountId");

        result.Should().Be("https://api.example.com/{{tenant}}/users/{accountId}");
    }

    [Fact]
    public void RenamePathParam_ColonForm_TreatsReplacementAsLiteral()
    {
        var result = PathTemplateHelper.RenamePathParam(
            "https://api.example.com/users/:id", "id", "user$1");

        result.Should().Be("https://api.example.com/users/:user$1");
    }

    // ── Colon syntax (Bruno) ─────────────────────────────────────────────────

    [Fact]
    public void ExtractPathParamNamesColon_FindsDistinctNamesInOrder()
    {
        var names = PathTemplateHelper.ExtractPathParamNamesColon(
            "https://api.example.com/users/:id/orders/:orderId/:id");

        names.Should().ContainInOrder("id", "orderId");
    }

    [Fact]
    public void ExtractPathParamNamesColon_DoesNotMatchPortNumbers()
    {
        var names = PathTemplateHelper.ExtractPathParamNamesColon(
            "https://api.example.com:8080/users/:id");

        names.Should().Equal("id");
    }

    [Fact]
    public void ExtractPathParamNamesColon_DoesNotMatchScheme()
    {
        var names = PathTemplateHelper.ExtractPathParamNamesColon(
            "https://api.example.com/users/:userId");

        // https: and api.example.com are not matched; only :userId in path
        names.Should().Equal("userId");
    }

    [Fact]
    public void ExtractPathParamNamesColon_IgnoresQueryAndFragment()
    {
        var names = PathTemplateHelper.ExtractPathParamNamesColon(
            "https://api.example.com/users/:id?x=:query#frag/:part");

        names.Should().Equal("id");
    }

    [Fact]
    public void ExtractPathParamNamesColon_EmptyInput_ReturnsEmpty()
    {
        PathTemplateHelper.ExtractPathParamNamesColon(string.Empty).Should().BeEmpty();
        PathTemplateHelper.ExtractPathParamNamesColon("  ").Should().BeEmpty();
    }

    [Fact]
    public void ApplyPathParamsColon_ReplacesKnownPlaceholders()
    {
        var result = PathTemplateHelper.ApplyPathParamsColon(
            "https://api.example.com/users/:id/orders/:orderId",
            new Dictionary<string, string>
            {
                ["id"] = "42",
                ["orderId"] = "abc",
            });

        result.Should().Be("https://api.example.com/users/42/orders/abc");
    }

    [Fact]
    public void ApplyPathParamsColon_LeavesUnknownPlaceholderUnchanged()
    {
        var result = PathTemplateHelper.ApplyPathParamsColon(
            "https://api.example.com/users/:id/orders/:orderId",
            new Dictionary<string, string> { ["id"] = "42" });

        result.Should().Be("https://api.example.com/users/42/orders/:orderId");
    }

    [Fact]
    public void ApplyPathParamsColon_PreservesQueryAndFragment()
    {
        var result = PathTemplateHelper.ApplyPathParamsColon(
            "https://api.example.com/users/:id?x=1#details",
            new Dictionary<string, string> { ["id"] = "42" });

        result.Should().Be("https://api.example.com/users/42?x=1#details");
    }
}
