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
/// <see cref="IEnvironmentMergeService.BuildStaticMerge"/> synchronously, substitutes
/// static variables (including those with empty values) normally, and leaves dynamic-typed
/// variable <c>{{tokens}}</c> in the URL unmodified and un-urlencoded.
/// </summary>
public sealed class RequestTabViewModelPreviewUrlTests
{
    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void PreviewUrl_WithStaticVar_ResolvesImmediately()
    {
        // Static vars are resolved synchronously from BuildStaticMerge.
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
    public void PreviewUrl_WithEmptyStaticVar_SubstitutesEmptyString()
    {
        // A static var with an empty value should still be substituted (resulting in an
        // empty string in the URL) — only dynamic-typed vars leave their {{token}} intact.
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/empty-static.callsmith",
            Name = "empty-static",
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
                    Value = string.Empty,                    // static type, but empty value
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });

        // Static var with empty value → token IS replaced, result has no username segment.
        sut.PreviewUrl.Should().Be("https://example.com/users/");
    }

    [Fact]
    public void PreviewUrl_WithDynamicVarDirectlyInUrl_LeavesTokenUnmodified()
    {
        // Dynamic vars (ResponseBody, MockData) are excluded from substitution so their
        // {{token}} remains verbatim — not replaced with an empty string or URL-encoded.
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
            PathParams = new Dictionary<string, string> { ["username"] = "i-am-{{me}}" },
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

        // Path param is applied, but with the dynamic part unresolved
        sut.PreviewUrl.Should().Be("https://example.com/i-am-{{me}}");
    }

    [Fact]
    public void PreviewUrl_WithDynamicVarInQueryParam_LeavesTokenUnencoded()
    {
        // Query-param values that reference dynamic {{tokens}} must not be URL-encoded.
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

    [Fact]
    public void PreviewUrl_ActiveEnvDynamicVarOverridesGlobalStaticVar_LeavesTokenUnmodified()
    {
        // If the active env defines a dynamic var with the same name as a global static var,
        // the active env wins (three-pass precedence) and the token should remain unmodified.
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/override.callsmith",
            Name = "override",
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
                    Value = "global-user",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });

        sut.SetEnvironment(new EnvironmentModel
        {
            FilePath = "active.env.callsmith",
            Name = "dev",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "username",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,  // dynamic wins
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });

        // Active env's ResponseBody type wins → token is left intact.
        sut.PreviewUrl.Should().Be("https://example.com/users/{{username}}");
    }

    [Fact]
    public void PreviewUrl_ForceOverrideGlobalStaticVar_WinsOverActiveEnvDynamicVar()
    {
        // A force-override global static var takes final priority over an active-env
        // dynamic var with the same name — it should be substituted normally.
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/force-override.callsmith",
            Name = "force-override",
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
                    Value = "forced-user",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                    IsForceGlobalOverride = true,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        });

        sut.SetEnvironment(new EnvironmentModel
        {
            FilePath = "active.env.callsmith",
            Name = "dev",
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

        // Force-override static global wins → token is substituted.
        sut.PreviewUrl.Should().Be("https://example.com/users/forced-user");
    }
}
