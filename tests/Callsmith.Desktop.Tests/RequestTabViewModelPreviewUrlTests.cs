using System.Net.Http;
using Avalonia.Threading;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Verifies that <see cref="RequestTabViewModel.PreviewUrl"/> resolves dynamic
/// environment variables (ResponseBody, MockData) — not just static ones — so that
/// the URL preview matches what is actually sent.
/// </summary>
public sealed class RequestTabViewModelPreviewUrlTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private const int MaxPollAttempts = 300;
    private const int PollDelayMs     = 10;

    /// <summary>
    /// Pumps the Avalonia UI-thread dispatcher until <paramref name="condition"/>
    /// returns true or the timeout is reached.
    /// </summary>
    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            if (Dispatcher.UIThread.CheckAccess())
                Dispatcher.UIThread.RunJobs();
            else
                await Dispatcher.UIThread.InvokeAsync(static () => Dispatcher.UIThread.RunJobs());

            if (condition())
                return;

            await Task.Delay(PollDelayMs);
        }

        condition().Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetGlobalEnvironment_WithDynamicResolvedVar_ReflectsValueInPreviewUrl()
    {
        // Arrange – set up a merge service that returns a dynamic-resolved variable value
        // (simulating a ResponseBody var whose cached value is already known).
        const string resolvedUsername = "johndoe";

        var mergeService = Substitute.For<IEnvironmentMergeService>();
        mergeService
            .BuildStaticMerge(Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel?>())
            .Returns(new Dictionary<string, string> { ["me"] = string.Empty });
        mergeService
            .MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["me"] = resolvedUsername },
            }));

        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            mergeService: mergeService);

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/preview.callsmith",
            Name = "preview",
            Method = HttpMethod.Get,
            Url = "https://example.com/{username}",
            PathParams = new Dictionary<string, string> { ["username"] = "{{me}}" },
        });

        // Trigger the async preview environment refresh.
        sut.SetGlobalEnvironment(new EnvironmentModel
        {
            FilePath = "global.env.callsmith",
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "me",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });

        // Wait for RefreshPreviewEnvAsync to complete and post the UI update.
        await AssertEventuallyAsync(() =>
            sut.PreviewUrl == $"https://example.com/{resolvedUsername}");

        sut.PreviewUrl.Should().Be($"https://example.com/{resolvedUsername}");
    }

    [Fact]
    public async Task SetEnvironment_WithDynamicResolvedVar_ReflectsValueInQueryParamPreview()
    {
        // Arrange – simulate a dynamic var used as a query param value.
        const string resolvedToken = "secret-token-123";

        var mergeService = Substitute.For<IEnvironmentMergeService>();
        mergeService
            .BuildStaticMerge(Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel?>())
            .Returns(new Dictionary<string, string>());
        mergeService
            .MergeAsync(Arg.Any<string>(), Arg.Any<EnvironmentModel>(), Arg.Any<EnvironmentModel?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["token"] = resolvedToken },
            }));

        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            mergeService: mergeService);

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/preview.callsmith",
            Name = "preview",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/data",
            QueryParams = [new RequestKv("auth", "{{token}}")],
        });

        // Trigger the async preview environment refresh.
        sut.SetEnvironment(new EnvironmentModel
        {
            FilePath = "active.env.callsmith",
            Name = "dev",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "token",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });

        // Wait for RefreshPreviewEnvAsync to complete and post the UI update.
        await AssertEventuallyAsync(() =>
            sut.PreviewUrl.Contains(resolvedToken));

        sut.PreviewUrl.Should().Contain($"auth={resolvedToken}");
    }

    [Fact]
    public async Task PreviewUrl_WithStaticVars_StillResolvesAfterEnvRefresh()
    {
        // Static vars should resolve once the async preview refresh completes.
        // This test verifies the basic (non-dynamic) case is not regressed.
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/static.callsmith",
            Name = "static",
            Method = HttpMethod.Get,
            Url = "https://{{host}}/users",
            QueryParams = [],
            PathParams = new Dictionary<string, string>(),
        });

        sut.SetGlobalEnvironment(new EnvironmentModel
        {
            FilePath = "global.env.callsmith",
            Name = "Global",
            Variables = [new EnvironmentVariable { Name = "host", Value = "api.example.com" }],
            EnvironmentId = Guid.NewGuid(),
        });

        // After an async refresh, the static var should be resolved in the preview.
        await AssertEventuallyAsync(() =>
            sut.PreviewUrl == "https://api.example.com/users");

        sut.PreviewUrl.Should().Be("https://api.example.com/users");
    }
}
