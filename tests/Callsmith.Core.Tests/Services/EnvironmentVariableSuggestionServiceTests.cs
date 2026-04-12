using Callsmith.Core.Models;
using Callsmith.Core.Services;
using FluentAssertions;

namespace Callsmith.Core.Tests.Services;

public sealed class EnvironmentVariableSuggestionServiceTests
{
    [Fact]
    public void Build_MergesLayersAndLetsLaterLayersOverride()
    {
        var sut = new EnvironmentVariableSuggestionService();

        var global = new[]
        {
            new EnvironmentVariable { Name = "baseUrl", Value = "https://global.example" },
            new EnvironmentVariable { Name = "token", Value = "global-token", IsSecret = true },
        };
        var active = new[]
        {
            new EnvironmentVariable { Name = "baseUrl", Value = "https://active.example" },
            new EnvironmentVariable { Name = "region", Value = "us-east-1" },
        };

        var result = sut.Build(global, active);

        result.Should().ContainEquivalentOf(new EnvironmentVariableSuggestion("baseUrl", "https://active.example"));
        result.Should().ContainEquivalentOf(new EnvironmentVariableSuggestion("region", "us-east-1"));
        result.Should().ContainEquivalentOf(new EnvironmentVariableSuggestion("token", "*****"));
    }

    [Fact]
    public void Build_SkipsBlankNames_AndSortsByName()
    {
        var sut = new EnvironmentVariableSuggestionService();

        var layer = new[]
        {
            new EnvironmentVariable { Name = " zeta ", Value = "3" },
            new EnvironmentVariable { Name = "", Value = "skip" },
            new EnvironmentVariable { Name = " alpha ", Value = "1" },
        };

        var result = sut.Build(layer);

        result.Select(r => r.Name).Should().Equal("alpha", "zeta");
    }
}
