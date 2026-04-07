using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

/// <summary>
/// Unit tests for <see cref="CurlCommandBuilder"/>.
/// </summary>
public sealed class CurlCommandBuilderTests
{
    private static RequestModel SimplePostRequest(string url, string body) => new()
    {
        Method = HttpMethod.Post,
        Url = url,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Body = body,
    };

    private static RequestModel GetRequest(string url) => new()
    {
        Method = HttpMethod.Get,
        Url = url,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    // ── Secret value masking — body ───────────────────────────────────────────

    [Fact]
    public void Build_WhenMaskAuthenticationTrue_ReplacesSecretValueInBodyWithPlaceholder()
    {
        var request = SimplePostRequest(
            "https://api.example.com/token",
            "grant_type=client_credentials&client_id=my-client-id&client_secret=s3cr3t");

        var secretValues = new HashSet<string> { "my-client-id", "s3cr3t" };

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            secretValues: secretValues);

        result.Should().Contain("client_id=<secret>");
        result.Should().Contain("client_secret=<secret>");
        result.Should().NotContain("my-client-id");
        result.Should().NotContain("s3cr3t");
    }

    [Fact]
    public void Build_WhenMaskAuthenticationFalse_ShowsSecretValuesInBody()
    {
        var request = SimplePostRequest(
            "https://api.example.com/token",
            "grant_type=client_credentials&client_id=my-client-id&client_secret=s3cr3t");

        var secretValues = new HashSet<string> { "my-client-id", "s3cr3t" };

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: false,
            secretValues: secretValues);

        result.Should().Contain("my-client-id");
        result.Should().Contain("s3cr3t");
        result.Should().NotContain("<secret>");
    }

    [Fact]
    public void Build_WhenSecretValuesNull_DoesNotMaskBody()
    {
        var request = SimplePostRequest(
            "https://api.example.com/token",
            "client_secret=s3cr3t");

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            secretValues: null);

        result.Should().Contain("s3cr3t");
        result.Should().NotContain("<secret>");
    }

    [Fact]
    public void Build_WhenSecretValuesEmpty_DoesNotMaskBody()
    {
        var request = SimplePostRequest(
            "https://api.example.com/token",
            "client_secret=s3cr3t");

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            secretValues: new HashSet<string>());

        result.Should().Contain("s3cr3t");
        result.Should().NotContain("<secret>");
    }

    // ── Secret value masking — URL ────────────────────────────────────────────

    [Fact]
    public void Build_WhenMaskAuthenticationTrue_ReplacesSecretValueInUrlWithPlaceholder()
    {
        var request = GetRequest("https://api.example.com/data?token=s3cr3t&format=json");
        var secretValues = new HashSet<string> { "s3cr3t" };

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            secretValues: secretValues);

        result.Should().Contain("<secret>");
        result.Should().NotContain("s3cr3t");
    }

    // ── Secret value masking — headers ────────────────────────────────────────

    [Fact]
    public void Build_WhenMaskAuthenticationTrue_ReplacesSecretValueInHeaderWithPlaceholder()
    {
        var request = new RequestModel
        {
            Method = HttpMethod.Get,
            Url = "https://api.example.com/data",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Custom-Token"] = "my-secret-token",
            },
        };
        var secretValues = new HashSet<string> { "my-secret-token" };

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            secretValues: secretValues);

        result.Should().Contain("X-Custom-Token: <secret>");
        result.Should().NotContain("my-secret-token");
    }

    // ── Secret value masking does not affect non-secret content ──────────────

    [Fact]
    public void Build_SecretMasking_DoesNotAffectNonSecretBodyContent()
    {
        var request = SimplePostRequest(
            "https://api.example.com/token",
            "grant_type=client_credentials&client_id=my-client-id&client_secret=s3cr3t");

        var secretValues = new HashSet<string> { "s3cr3t" };

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            secretValues: secretValues);

        result.Should().Contain("grant_type=client_credentials");
        result.Should().Contain("my-client-id");
        result.Should().Contain("client_secret=<secret>");
    }

    // ── Combined: auth masking + secret masking ───────────────────────────────

    [Fact]
    public void Build_WhenBothAuthAndSecretMaskingActive_MasksAll()
    {
        var request = new RequestModel
        {
            Method = HttpMethod.Post,
            Url = "https://api.example.com/token",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer real-bearer-token",
            },
            Body = "client_secret=s3cr3t",
        };
        var secretValues = new HashSet<string> { "s3cr3t" };

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            authMaskInfo: null,
            secretValues: secretValues);

        result.Should().Contain("Bearer <token>");
        result.Should().Contain("client_secret=<secret>");
        result.Should().NotContain("real-bearer-token");
        result.Should().NotContain("s3cr3t");
    }

    // ── Edge case: empty secret value is ignored ──────────────────────────────

    [Fact]
    public void Build_EmptySecretValue_IsIgnoredAndDoesNotCorruptOutput()
    {
        var request = SimplePostRequest(
            "https://api.example.com/token",
            "data=hello");

        var secretValues = new HashSet<string> { string.Empty, "hello" };

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            secretValues: secretValues);

        result.Should().Contain("data=<secret>");
        // Ensure empty string replacement did not corrupt the output
        result.Should().NotContain("<secret><secret>");
    }

    // ── Substring ordering: longer secret takes priority ─────────────────────

    [Fact]
    public void Build_WhenSecretIsSubstringOfAnother_LongerSecretMaskedFirst()
    {
        // "abc" is a substring of "abcxyz" — both are secrets.
        // Longest-first replacement ensures "abcxyz" is masked as a whole,
        // not as "<secret>xyz" (which would happen if "abc" were replaced first).
        var request = SimplePostRequest(
            "https://api.example.com/token",
            "val1=abcxyz&val2=abc");

        var secretValues = new HashSet<string> { "abc", "abcxyz" };

        var result = CurlCommandBuilder.Build(
            request,
            maskAuthentication: true,
            secretValues: secretValues);

        result.Should().Contain("val1=<secret>");
        result.Should().Contain("val2=<secret>");
        result.Should().NotContain("abcxyz");
        result.Should().NotContain("=<secret>xyz");
    }
}
