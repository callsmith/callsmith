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
}
