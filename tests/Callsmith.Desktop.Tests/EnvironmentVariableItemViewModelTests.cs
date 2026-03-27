using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for <see cref="EnvironmentVariableItemViewModel"/>.
/// Verifies variable type options and restrictions for Bruno concrete environments.
/// </summary>
public sealed class EnvironmentVariableItemViewModelTests
{
    // ─── Tests: Available type options ────────────────────────────────────────

    [Fact]
    public void AvailableVariableTypeOptions_WhenNotBrunoConcreteEnv_ReturnsAllThreeTypes()
    {
        // Arrange
        var vm = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment())
        {
            IsBrunoConcreteEnvironment = false,
        };

        // Act
        var options = vm.AvailableVariableTypeOptions;

        // Assert
        options.Should().HaveCount(3);
        options.Should().Equal(["Static", "Mock Data", "Response Body Value"]);
    }

    [Fact]
    public void AvailableVariableTypeOptions_WhenBrunoConcreteEnv_ReturnsOnlyStatic()
    {
        // Arrange
        var vm = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment())
        {
            IsBrunoConcreteEnvironment = true,
        };

        // Act
        var options = vm.AvailableVariableTypeOptions;

        // Assert
        options.Should().HaveCount(1);
        options.Should().Equal(["Static"]);
    }

    // ─── Tests: Type selection restrictions ───────────────────────────────────

    [Fact]
    public void VariableTypeDisplay_WhenBrunoConcreteEnv_ForcesTypeToStatic()
    {
        // Arrange
        var vm = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment())
        {
            VariableType = EnvironmentVariable.VariableTypes.Static,
            IsBrunoConcreteEnvironment = true,
        };

        // Act - Try to set to Mock Data
        vm.VariableTypeDisplay = "Mock Data";

        // Assert - Should remain Static
        vm.VariableType.Should().Be(EnvironmentVariable.VariableTypes.Static);
        vm.VariableTypeDisplay.Should().Be("Static");
    }

    [Fact]
    public void VariableTypeDisplay_WhenBrunoConcreteEnv_ForcesTypeToStaticForResponseBody()
    {
        // Arrange
        var vm = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment())
        {
            VariableType = EnvironmentVariable.VariableTypes.Static,
            IsBrunoConcreteEnvironment = true,
        };

        // Act - Try to set to Response Body
        vm.VariableTypeDisplay = "Response Body Value";

        // Assert - Should remain Static
        vm.VariableType.Should().Be(EnvironmentVariable.VariableTypes.Static);
        vm.VariableTypeDisplay.Should().Be("Static");
    }

    [Fact]
    public void VariableTypeDisplay_WhenNotBrunoConcreteEnv_AllowsTypeChange()
    {
        // Arrange
        var vm = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment())
        {
            VariableType = EnvironmentVariable.VariableTypes.Static,
            IsBrunoConcreteEnvironment = false,
        };

        // Act - Set to Mock Data
        vm.VariableTypeDisplay = "Mock Data";

        // Assert - Should change to Mock Data
        vm.VariableType.Should().Be(EnvironmentVariable.VariableTypes.MockData);
        vm.VariableTypeDisplay.Should().Be("Mock Data");
    }

    [Fact]
    public void VariableTypeDisplay_WhenNotBrunoConcreteEnv_AllowsResponseBodyType()
    {
        // Arrange
        var vm = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment())
        {
            VariableType = EnvironmentVariable.VariableTypes.Static,
            IsBrunoConcreteEnvironment = false,
        };

        // Act - Set to Response Body
        vm.VariableTypeDisplay = "Response Body Value";

        // Assert - Should change to Response Body
        vm.VariableType.Should().Be(EnvironmentVariable.VariableTypes.ResponseBody);
        vm.VariableTypeDisplay.Should().Be("Response Body Value");
    }

    [Fact]
    public void VariableTypeDisplay_IgnoresNullValue()
    {
        // Arrange
        var vm = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment())
        {
            VariableType = EnvironmentVariable.VariableTypes.Static,
            IsBrunoConcreteEnvironment = false,
        };

        // Act - Try to set null
        vm.VariableTypeDisplay = null!;

        // Assert - Should remain unchanged
        vm.VariableType.Should().Be(EnvironmentVariable.VariableTypes.Static);
        vm.VariableTypeDisplay.Should().Be("Static");
    }

    [Fact]
    public void IsStatic_ReflectsCurrentType()
    {
        // Arrange
        var vm = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment());

        // Act & Assert
        vm.VariableType = EnvironmentVariable.VariableTypes.Static;
        vm.IsStatic.Should().BeTrue();
        vm.IsMockData.Should().BeFalse();
        vm.IsResponseBody.Should().BeFalse();

        vm.VariableType = EnvironmentVariable.VariableTypes.MockData;
        vm.IsStatic.Should().BeFalse();
        vm.IsMockData.Should().BeTrue();
        vm.IsResponseBody.Should().BeFalse();

        vm.VariableType = EnvironmentVariable.VariableTypes.ResponseBody;
        vm.IsStatic.Should().BeFalse();
        vm.IsMockData.Should().BeFalse();
        vm.IsResponseBody.Should().BeTrue();
    }
}
