using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using FluentAssertions;

namespace Callsmith.Core.Tests;

public sealed class EnvironmentModelEqualityComparerTests
{
    [Fact]
    public void Equals_WhenModelsMatch_ReturnsTrue()
    {
        var left = CreateModel();
        var right = CreateModel();

        EnvironmentModelEqualityComparer.Instance.Equals(left, right).Should().BeTrue();
    }

    [Fact]
    public void Equals_WhenVariableOrderDiffers_ReturnsFalse()
    {
        var left = CreateModel();
        var right = CreateModel() with
        {
            Variables = [left.Variables[1], left.Variables[0]],
        };

        EnvironmentModelEqualityComparer.Instance.Equals(left, right).Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenColorDiffers_ReturnsFalse()
    {
        var left = CreateModel();
        var right = CreateModel() with { Color = "#ffffff" };

        EnvironmentModelEqualityComparer.Instance.Equals(left, right).Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenDynamicMetadataDiffers_ReturnsFalse()
    {
        var left = CreateModel();
        var right = CreateModel() with
        {
            Variables =
            [
                left.Variables[0],
                new EnvironmentVariable
                {
                    Name = "access-token",
                    Value = "cached-token",
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                    ResponseRequestName = "Auth/login",
                    ResponsePath = "$.token",
                    ResponseMatcher = ResponseValueMatcher.JsonPath,
                    ResponseFrequency = DynamicFrequency.IfExpired,
                    ResponseExpiresAfterSeconds = 120,
                },
            ],
        };

        EnvironmentModelEqualityComparer.Instance.Equals(left, right).Should().BeFalse();
    }

    private static EnvironmentModel CreateModel() => new()
    {
        FilePath = @"c:\collections\environment\dev.env.callsmith",
        EnvironmentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Name = "dev",
        Color = "#4ec9b0",
        GlobalPreviewEnvironmentName = "dev",
        Variables =
        [
            new EnvironmentVariable
            {
                Name = "base-url",
                Value = "https://api.dev",
                VariableType = EnvironmentVariable.VariableTypes.Static,
            },
            new EnvironmentVariable
            {
                Name = "access-token",
                Value = "cached-token",
                VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                ResponseRequestName = "Auth/login",
                ResponsePath = "$.token",
                ResponseMatcher = ResponseValueMatcher.JsonPath,
                ResponseFrequency = DynamicFrequency.IfExpired,
                ResponseExpiresAfterSeconds = 900,
            },
        ],
    };
}