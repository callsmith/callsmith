using Callsmith.Core.Abstractions;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="EnvironmentMergeService"/>.
/// Verifies the three-layer precedence rules and dynamic variable resolution
/// that are shared between the send pipeline and the environment editor preview.
/// </summary>
public sealed class EnvironmentMergeServiceTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static EnvironmentModel GlobalEnv(params EnvironmentVariable[] vars) =>
        new() { FilePath = "global.env.callsmith", Name = "Global", Variables = [..vars], EnvironmentId = Guid.NewGuid() };

    private static EnvironmentModel ActiveEnv(params EnvironmentVariable[] vars) =>
        new() { FilePath = "dev.env.callsmith", Name = "Dev", Variables = [..vars], EnvironmentId = Guid.NewGuid() };

    private static EnvironmentVariable Static(string name, string value, bool forceOverride = false) =>
        new() { Name = name, Value = value, VariableType = EnvironmentVariable.VariableTypes.Static, IsForceGlobalOverride = forceOverride };

    private static EnvironmentVariable MockDataVar(string name) =>
        new() { Name = name, Value = string.Empty, VariableType = EnvironmentVariable.VariableTypes.MockData, MockDataCategory = "Internet", MockDataField = "Email" };

    private static EnvironmentVariable ResponseBodyVar(string name, string requestName = "get-token") =>
        new() { Name = name, Value = string.Empty, VariableType = EnvironmentVariable.VariableTypes.ResponseBody, ResponseRequestName = requestName };

    // ─── BuildStaticMerge ────────────────────────────────────────────────────

    [Fact]
    public void BuildStaticMerge_GlobalOnly_ReturnsGlobalVars()
    {
        var sut = new EnvironmentMergeService();
        var global = GlobalEnv(Static("base-url", "https://api.example.com"), Static("timeout", "30"));

        var result = sut.BuildStaticMerge(global, null);

        result.Should().ContainKey("base-url").WhoseValue.Should().Be("https://api.example.com");
        result.Should().ContainKey("timeout").WhoseValue.Should().Be("30");
    }

    [Fact]
    public void BuildStaticMerge_ActiveEnvOverridesGlobal()
    {
        var sut = new EnvironmentMergeService();
        var global = GlobalEnv(Static("base-url", "https://api.example.com"), Static("shared", "global-value"));
        var active = ActiveEnv(Static("base-url", "https://api.dev.com"), Static("active-only", "only-in-active"));

        var result = sut.BuildStaticMerge(global, active);

        result["base-url"].Should().Be("https://api.dev.com");       // active wins
        result["shared"].Should().Be("global-value");                  // global carries through
        result["active-only"].Should().Be("only-in-active");           // active-only included
    }

    [Fact]
    public void BuildStaticMerge_ForceOverrideGlobalWinsOverActive()
    {
        var sut = new EnvironmentMergeService();
        var global = GlobalEnv(Static("api-version", "v3", forceOverride: true));
        var active = ActiveEnv(Static("api-version", "v2"));

        var result = sut.BuildStaticMerge(global, active);

        result["api-version"].Should().Be("v3"); // force-override wins
    }

    [Fact]
    public void BuildStaticMerge_NonForceOverrideGlobalYieldsToActive()
    {
        var sut = new EnvironmentMergeService();
        var global = GlobalEnv(Static("env-name", "production"));
        var active = ActiveEnv(Static("env-name", "development"));

        var result = sut.BuildStaticMerge(global, active);

        result["env-name"].Should().Be("development"); // active wins (no force-override)
    }

    // ─── MergeAsync — no evaluator ───────────────────────────────────────────

    [Fact]
    public async Task MergeAsync_NoEvaluator_ReturnsStaticMerge()
    {
        var sut = new EnvironmentMergeService(evaluator: null);
        var global = GlobalEnv(Static("base-url", "https://api.example.com"));
        var active = ActiveEnv(Static("key", "value"));

        var result = await sut.MergeAsync("C:/collection", global, active);

        result.Variables["base-url"].Should().Be("https://api.example.com");
        result.Variables["key"].Should().Be("value");
        result.MockGenerators.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeAsync_NoEvaluator_NullActiveEnv_ReturnsGlobalVars()
    {
        var sut = new EnvironmentMergeService(evaluator: null);
        var global = GlobalEnv(Static("token", "abc123"));

        var result = await sut.MergeAsync("C:/collection", global, null);

        result.Variables["token"].Should().Be("abc123");
    }

    // ─── MergeAsync — dynamic evaluation ────────────────────────────────────

    [Fact]
    public async Task MergeAsync_ResolvesGlobalDynamicVars()
    {
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["token"] = "Bearer xyz" },
                MockGenerators = new Dictionary<string, MockDataEntry>(),
            });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(ResponseBodyVar("token"));
        var active = ActiveEnv(Static("base-url", "https://api.dev.com"));

        var result = await sut.MergeAsync("C:/collection", global, active);

        result.Variables["token"].Should().Be("Bearer xyz");
        result.Variables["base-url"].Should().Be("https://api.dev.com");
    }

    [Fact]
    public async Task MergeAsync_GlobalCacheNamespace_UsesActiveEnvId_WhenActivePresent()
    {
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new ResolvedEnvironment { Variables = new Dictionary<string, string>() });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(ResponseBodyVar("token"));
        var active = ActiveEnv(Static("x", "y"));

        await sut.MergeAsync("C:/col", global, active);

        // The global resolve call must use the active env's ID as the cache namespace
        // (unified namespace — same as the concrete env's own cache key).
        await evaluator.Received().ResolveAsync(
            Arg.Any<string>(),
            Arg.Is<string>(ns => ns == active.EnvironmentId.ToString("N")),
            Arg.Is<IReadOnlyList<EnvironmentVariable>>(v => v == global.Variables),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MergeAsync_GlobalCacheNamespace_IsGlobalIdOnly_WhenNoActiveEnv()
    {
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new ResolvedEnvironment { Variables = new Dictionary<string, string>() });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(ResponseBodyVar("token"));

        await sut.MergeAsync("C:/col", global, null);

        await evaluator.Received().ResolveAsync(
            Arg.Any<string>(),
            Arg.Is<string>(ns => ns == global.EnvironmentId.ToString("N")),
            Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MergeAsync_ActiveEnvStaticsWinOverResolvedGlobals()
    {
        // Global has a response-body var "base-url". After resolution it becomes "https://api.global.com".
        // Active env has a STATIC "base-url" = "https://api.dev.com".
        // The active static must win (re-applied after global resolution).
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Is<IReadOnlyList<EnvironmentVariable>>(v => v.Any(x => x.Name == "base-url")),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["base-url"] = "https://api.global.com" },
            });
        // Active env resolve call returns nothing extra.
        evaluator.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Is<IReadOnlyList<EnvironmentVariable>>(v => !v.Any(x => x.Name == "base-url")),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment { Variables = new Dictionary<string, string>() });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(ResponseBodyVar("base-url"));
        var active = ActiveEnv(Static("base-url", "https://api.dev.com"));

        var result = await sut.MergeAsync("C:/col", global, active);

        result.Variables["base-url"].Should().Be("https://api.dev.com"); // active static wins
    }

    [Fact]
    public async Task MergeAsync_ForceOverrideDynamicGlobalWinsOverActiveAtEnd()
    {
        // Global has a FORCE-OVERRIDE response-body var "token".
        // After global resolution it becomes "Bearer-global".
        // Active env also has a static "token" = "Bearer-active".
        // Force-override global must win at the final step.
        var globalId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        // Global resolution call — now uses the active env's ID as the namespace (unified cache).
        evaluator.ResolveAsync(
                Arg.Any<string>(),
                Arg.Is<string>(ns => ns == activeId.ToString("N")),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["token"] = "Bearer-global" },
            });
        // Active resolution call — also uses the active env's ID but with the active env's own variables.
        // Since both namespaces are the same, NSubstitute will use the first matching mock for both.
        // We suppress the active call by returning empty for that variable list specifically.
        evaluator.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Is<IReadOnlyList<EnvironmentVariable>>(v => v.Any(x => x.Name == "token" && x.VariableType == EnvironmentVariable.VariableTypes.Static)),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment { Variables = new Dictionary<string, string>() });

        var sut = new EnvironmentMergeService(evaluator);
        var global = new EnvironmentModel
        {
            FilePath = "global.env.callsmith",
            Name = "Global",
            EnvironmentId = globalId,
            Variables = [new EnvironmentVariable
            {
                Name = "token",
                Value = string.Empty,
                VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                ResponseRequestName = "get-token",
                IsForceGlobalOverride = true,
            }],
        };
        var active = new EnvironmentModel
        {
            FilePath = "dev.env.callsmith",
            Name = "Dev",
            EnvironmentId = activeId,
            Variables = [Static("token", "Bearer-active")],
        };

        var result = await sut.MergeAsync("C:/col", global, active);

        result.Variables["token"].Should().Be("Bearer-global"); // force-override wins
    }

    [Fact]
    public async Task MergeAsync_ActiveDynamicVarsAreResolved()
    {
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        // Global has no dynamic vars → only active resolution is called.
        evaluator.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["generated-id"] = "uuid-123" },
            });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(Static("base-url", "https://api.example.com"));
        var active = ActiveEnv(ResponseBodyVar("generated-id", "create-item"));

        var result = await sut.MergeAsync("C:/col", global, active);

        result.Variables["generated-id"].Should().Be("uuid-123");
    }

    [Fact]
    public async Task MergeAsync_ActiveDynamicCacheNamespace_IsActiveEnvId()
    {
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new ResolvedEnvironment { Variables = new Dictionary<string, string>() });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(Static("x", "y")); // no dynamic → only active call occurs
        var active = ActiveEnv(ResponseBodyVar("session-id"));

        await sut.MergeAsync("C:/col", global, active);

        await evaluator.Received().ResolveAsync(
            Arg.Any<string>(),
            Arg.Is<string>(ns => ns == active.EnvironmentId.ToString("N")),
            Arg.Is<IReadOnlyList<EnvironmentVariable>>(v => v == active.Variables),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MergeAsync_MockGeneratorsAreMerged()
    {
        var mockEntry = MockDataCatalog.All.First(e => e.Category == "Internet" && e.Field == "Email");
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string>(),
                MockGenerators = new Dictionary<string, MockDataEntry> { ["fake-email"] = mockEntry },
            });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(MockDataVar("fake-email"));
        var active = ActiveEnv(Static("x", "y"));

        var result = await sut.MergeAsync("C:/col", global, active);

        result.MockGenerators.Should().ContainKey("fake-email");
        result.MockGenerators["fake-email"].Should().Be(mockEntry);
    }

    [Fact]
    public async Task MergeAsync_WhenEvaluatorThrows_FallsBackToStaticMerge()
    {
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("network error"));

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(Static("base-url", "https://api.example.com"), ResponseBodyVar("token"));
        var active = ActiveEnv(Static("key", "value"));

        var result = await sut.MergeAsync("C:/col", global, active);

        // Static values must still be present even after evaluation failure.
        result.Variables["base-url"].Should().Be("https://api.example.com");
        result.Variables["key"].Should().Be("value");
    }

    [Fact]
    public async Task MergeAsync_WhenCancelled_ThrowsOperationCancelledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(ResponseBodyVar("token"));

        await sut.Invoking(s => s.MergeAsync("C:/col", global, null, ct: cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── Precedence table ────────────────────────────────────────────────────

    [Theory]
    [InlineData("global-only", "g-val", null, false, "g-val")]
    [InlineData("shared", "g-val", "a-val", false, "a-val")]       // active wins
    [InlineData("shared", "g-val", "a-val", true, "g-val")]         // force-override wins
    [InlineData("global-only", "g-val", null, true, "g-val")]       // force-override, no conflict
    public void BuildStaticMerge_PrecedenceTable(
        string varName, string globalValue, string? activeValue, bool forceOverride, string expectedResult)
    {
        var sut = new EnvironmentMergeService();
        var globalVars = new List<EnvironmentVariable> { Static(varName, globalValue, forceOverride) };
        var global = new EnvironmentModel { FilePath = "g.callsmith", Name = "G", Variables = globalVars, EnvironmentId = Guid.NewGuid() };

        EnvironmentModel? active = null;
        if (activeValue is not null)
            active = ActiveEnv(Static(varName, activeValue));

        var result = sut.BuildStaticMerge(global, active);

        result[varName].Should().Be(expectedResult);
    }

    // ─── Global context passed to evaluator ──────────────────────────────────

    [Fact]
    public async Task MergeAsync_GlobalResolutionReceives_ActiveEnvStaticsAsContext()
    {
        // The global response-body var needs the active env's base-url to make its HTTP call.
        // Verify that the merged dict passed to the global ResolveAsync includes active statics.
        IReadOnlyDictionary<string, string>? capturedContext = null;
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Do<IReadOnlyDictionary<string, string>>(ctx => capturedContext = ctx),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment { Variables = new Dictionary<string, string>() });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(ResponseBodyVar("token"));
        var active = ActiveEnv(Static("base-url", "https://api.dev.com"));

        await sut.MergeAsync("C:/col", global, active);

        capturedContext.Should().ContainKey("base-url").WhoseValue.Should().Be("https://api.dev.com");
    }

    // ─── allowStaleCache propagation ─────────────────────────────────────────

    [Fact]
    public async Task MergeAsync_AllowStaleCache_IsForwardedToEvaluator()
    {
        bool? capturedAllowStale = null;
        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Do<bool>(v => capturedAllowStale = v),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment { Variables = new Dictionary<string, string>() });

        var sut = new EnvironmentMergeService(evaluator);
        var global = GlobalEnv(ResponseBodyVar("token"));

        await sut.MergeAsync("C:/col", global, null, allowStaleCache: true);

        capturedAllowStale.Should().BeTrue("allowStaleCache = true must be forwarded to the evaluator");
    }
}
