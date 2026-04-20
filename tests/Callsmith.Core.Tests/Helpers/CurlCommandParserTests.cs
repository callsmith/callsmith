using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

public sealed class CurlCommandParserTests
{
    [Fact]
    public void TryParse_ReturnsFalse_ForNonCurlText()
    {
        var ok = CurlCommandParser.TryParse("https://api.example.com/users", out var parsed);

        ok.Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void TryParse_ReturnsFalse_ForTextStartingWithCurlButNoSpace()
    {
        // "curlsmith" starts with "curl" but the 5th character is not whitespace.
        var ok = CurlCommandParser.TryParse("curlsmith https://api.example.com", out var parsed);

        ok.Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void TryParse_IgnoresUnknownValueFlagAndItsValue_PicksUpUrl()
    {
        // --limit-rate is not a recognized flag; 200K should not be treated as the URL.
        var command = "curl --limit-rate 200K https://api.example.com/data";

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Url.Should().Be("https://api.example.com/data");
        parsed.Method.Should().Be("GET");
    }

    [Fact]
    public void TryParse_IgnoresUnknownValueFlagAndItsValue_WithDownloadFlag()
    {
        // -O takes no value in curl, but since it is unknown to us the next token is peeked.
        // https://... contains "://" so -O is skipped alone, and the URL is still found.
        var command = "curl --limit-rate 200K -O https://example.com/file.zip";

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Url.Should().Be("https://example.com/file.zip");
    }

    [Fact]
    public void TryParse_IgnoresUnknownNoValueFlag_Verbose()
    {
        // -v is a well-known curl flag that takes no value.
        var command = "curl -v -X POST https://api.example.com/items -H \"Content-Type: application/json\"";

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Method.Should().Be("POST");
        parsed.Url.Should().Be("https://api.example.com/items");
        parsed.Headers.Should().ContainSingle(h => h.Key == "Content-Type");
    }

    [Fact]
    public void TryParse_ParsesBasicCurlRequest()
    {
        var command = """
                      curl -X POST "https://api.example.com/users?active=true" \
                        -H "Content-Type: application/json" \
                        -H "X-Trace: abc" \
                        -d '{"name":"alice"}'
                      """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Method.Should().Be("POST");
        parsed.Url.Should().Be("https://api.example.com/users");
        parsed.QueryParams.Should().ContainSingle(p => p.Key == "active" && p.Value == "true");
        parsed.Headers.Should().Contain(p => p.Key == "Content-Type" && p.Value == "application/json");
        parsed.Headers.Should().Contain(p => p.Key == "X-Trace" && p.Value == "abc");
        parsed.BodyType.Should().Be(CollectionRequest.BodyTypes.Json);
        parsed.Body.Should().Be("""{"name":"alice"}""");
    }

    [Fact]
    public void TryParse_WithGetFlag_ConvertsDataToQueryParams()
    {
        var command = """curl "https://api.example.com/search" -G -d "q=callsmith" -d "page=2" """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Method.Should().Be("GET");
        parsed.BodyType.Should().Be(CollectionRequest.BodyTypes.None);
        parsed.Body.Should().BeNull();
        parsed.QueryParams.Should().Contain(p => p.Key == "q" && p.Value == "callsmith");
        parsed.QueryParams.Should().Contain(p => p.Key == "page" && p.Value == "2");
    }

    [Fact]
    public void TryParse_WithUserFlag_ParsesBasicAuth()
    {
        var command = """curl --url https://api.example.com/me -u "demo:secret" """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Basic);
        parsed.Auth.Username.Should().Be("demo");
        parsed.Auth.Password.Should().Be("secret");
    }

    [Fact]
    public void TryParse_WithUrlQueryFlag_AddsQueryParams()
    {
        var command = """curl https://api.example.com/search --url-query "q=hello" --url-query "page=1" """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.QueryParams.Should().Contain(p => p.Key == "q" && p.Value == "hello");
        parsed.QueryParams.Should().Contain(p => p.Key == "page" && p.Value == "1");
    }

    [Fact]
    public void TryParse_WithJsonFlag_SetsJsonBodyAndInfersJsonType()
    {
        var command = """curl https://api.example.com/users --json '{"name":"bob"}' """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.BodyType.Should().Be(CollectionRequest.BodyTypes.Json);
        parsed.Body.Should().Be("""{"name":"bob"}""");
        parsed.Method.Should().Be("POST");
    }

    [Fact]
    public void TryParse_WithJsonFlag_RespectsExplicitContentTypeHeader()
    {
        // If the user explicitly sets a Content-Type, it overrides the --json default.
        var command = """curl https://api.example.com/data --json '<?xml version="1.0"?><r/>' -H "Content-Type: application/xml" """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.BodyType.Should().Be(CollectionRequest.BodyTypes.Xml);
    }

    [Fact]
    public void TryParse_WithOauth2BearerFlag_SetsBearerAuth()
    {
        var command = """curl --oauth2-bearer "mytoken123" https://api.example.com/me """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Auth.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        parsed.Auth.Token.Should().Be("mytoken123");
    }

    [Fact]
    public void TryParse_WithUserAgentFlag_AddsUserAgentHeader()
    {
        var command = """curl -A "MyApp/1.0" https://api.example.com/data """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Headers.Should().ContainSingle(h => h.Key == "User-Agent" && h.Value == "MyApp/1.0");
    }

    [Fact]
    public void TryParse_WithLongUserAgentFlag_AddsUserAgentHeader()
    {
        var command = """curl --user-agent "curl/8.0" https://api.example.com/data """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Headers.Should().ContainSingle(h => h.Key == "User-Agent" && h.Value == "curl/8.0");
    }

    [Fact]
    public void TryParse_WithRefererFlag_AddsRefererHeader()
    {
        var command = """curl -e "https://referrer.example.com" https://api.example.com/data """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Headers.Should().ContainSingle(h => h.Key == "Referer" && h.Value == "https://referrer.example.com");
    }

    [Fact]
    public void TryParse_WithLongRefererFlag_AddsRefererHeader()
    {
        var command = """curl --referer "https://referrer.example.com" https://api.example.com/data """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Headers.Should().ContainSingle(h => h.Key == "Referer" && h.Value == "https://referrer.example.com");
    }

    [Fact]
    public void TryParse_WithCookieFlag_AddsCookieHeader()
    {
        var command = """curl -b "session=abc123; user=alice" https://api.example.com/profile """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Headers.Should().ContainSingle(h => h.Key == "Cookie" && h.Value == "session=abc123; user=alice");
    }

    [Fact]
    public void TryParse_WithLongCookieFlag_AddsCookieHeader()
    {
        var command = """curl --cookie "token=xyz" https://api.example.com/profile """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Headers.Should().ContainSingle(h => h.Key == "Cookie" && h.Value == "token=xyz");
    }

    [Fact]
    public void TryParse_WithFormFlag_SetsMultipartBody()
    {
        var command = """curl -F "name=alice" -F "age=30" https://api.example.com/users """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.BodyType.Should().Be(CollectionRequest.BodyTypes.Multipart);
        parsed.FormParams.Should().Contain(p => p.Key == "name" && p.Value == "alice");
        parsed.FormParams.Should().Contain(p => p.Key == "age" && p.Value == "30");
        parsed.Method.Should().Be("POST");
    }

    [Fact]
    public void TryParse_WithFormFlag_SkipsFileReferences()
    {
        var command = """curl -F "file=@photo.jpg" -F "caption=hello" https://api.example.com/upload """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.BodyType.Should().Be(CollectionRequest.BodyTypes.Multipart);
        // File reference (@photo.jpg) should be skipped; string field kept.
        parsed.FormParams.Should().NotContain(p => p.Key == "file");
        parsed.FormParams.Should().ContainSingle(p => p.Key == "caption" && p.Value == "hello");
    }

    [Fact]
    public void TryParse_WithFormStringFlag_TreatsAtPrefixAsLiteralString()
    {
        // --form-string always treats the value as a plain string, never as a file reference.
        var command = """curl --form-string "note=@not-a-file" https://api.example.com/items """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.BodyType.Should().Be(CollectionRequest.BodyTypes.Multipart);
        parsed.FormParams.Should().ContainSingle(p => p.Key == "note" && p.Value == "@not-a-file");
    }

    [Fact]
    public void TryParse_WithUrlQueryAndExistingQueryParam_CombinesBoth()
    {
        var command = """curl "https://api.example.com/search?existing=1" --url-query "added=2" """;

        var ok = CurlCommandParser.TryParse(command, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.QueryParams.Should().Contain(p => p.Key == "existing" && p.Value == "1");
        parsed.QueryParams.Should().Contain(p => p.Key == "added" && p.Value == "2");
    }
}
