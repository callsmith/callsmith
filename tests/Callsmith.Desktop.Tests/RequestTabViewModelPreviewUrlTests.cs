using System.Net.Http;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Verifies that <see cref="RequestTabViewModel.PreviewUrl"/> uses
/// <see cref="IEnvironmentMergeService.BuildStaticMerge"/> synchronously and leaves
/// unresolved dynamic-variable <c>{{tokens}}</c> in the URL unmodified and un-urlencoded.
/// </summary>
public sealed class RequestTabViewModelPreviewUrlTests
{
    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void PreviewUrl_WithStaticVar_ResolvesImmediately()
    {
        // Static vars are included in BuildStaticMerge with their configured value,
        // so they resolve synchronously — no async wait required.
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

        sut.PreviewUrl.Should().Be("https://api.example.com/users");
    }

    [Fact]
    public void PreviewUrl_WithDynamicVarDirectlyInUrl_LeavesTokenUnmodified()
    {
        // A dynamic var (ResponseBody / MockData) has Value = "" in BuildStaticMerge.
        // The empty value is filtered out so the {{token}} is left as-is in the URL.
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/dynamic.callsmith",
            Name = "dynamic",
            Method = HttpMethod.Get,
            Url = "https://example.com/users/{{username}}",
            QueryParams = [],
            PathParams = new Dictionary<string, string>(),
        });

        sut.SetGlobalEnvironment(new EnvironmentModel
        {
            FilePath = "global.env.callsmith",
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "username",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });

        // Token must appear verbatim — not empty and not URL-encoded.
        sut.PreviewUrl.Should().Be("https://example.com/users/{{username}}");
    }

    [Fact]
    public void PreviewUrl_WithDynamicVarInPathParam_LeavesPathPlaceholderUnresolved()
    {
        // When the path-param value is a dynamic {{token}}, the path placeholder {id}
        // stays in the URL rather than being replaced with URL-encoded braces.
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/preview.callsmith",
            Name = "preview",
            Method = HttpMethod.Get,
            Url = "https://example.com/{username}",
            PathParams = new Dictionary<string, string> { ["username"] = "{{me}}" },
        });

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

        // Path param is not applied because its value is unresolved; {username} stays.
        sut.PreviewUrl.Should().Be("https://example.com/{username}");
    }

    [Fact]
    public void PreviewUrl_WithDynamicVarInQueryParam_LeavesTokenUnencoded()
    {
        // Query-param values that are dynamic {{tokens}} must not be URL-encoded.
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/preview.callsmith",
            Name = "preview",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/data",
            QueryParams = [new RequestKv("auth", "{{token}}")],
        });

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

        sut.PreviewUrl.Should().Be("https://api.example.com/data?auth={{token}}");
    }
}
