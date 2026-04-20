using System.Net.Http;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class RequestViewModelPathParamsTests
{
    [Fact]
    public void RenamingPathParamKey_UpdatesUrlPlaceholderAndKeepsQueryString()
    {
        var collectionService = Substitute.For<ICollectionService>();
        var messenger = WeakReferenceMessenger.Default;

        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            collectionService,
            messenger,
            _ => { });

        var request = new CollectionRequest
        {
            FilePath = "c:/tmp/sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/users/{id}",
            QueryParams = [new RequestKv("include", "orders")],
            PathParams = new Dictionary<string, string> { ["id"] = "42" },
        };

        sut.LoadRequest(request);
        sut.PathParams.Items.Should().HaveCount(1);

        // Simulate editing the path-param key directly in the Params table.
        sut.PathParams.Items[0].Key = "userId";

        sut.Url.Should().Be("https://api.example.com/users/{userId}?include=orders");
        sut.PathParams.Items[0].Value.Should().Be("42");
    }

    [Fact]
    public void BrunoColonSyntax_LoadRequest_DetectsPathParams()
    {
        // Create a temp Bruno collection root with bruno.json so IsBrunoCollection returns true.
        var brunoRoot = Path.Combine(Path.GetTempPath(), "BrunoPathParamTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(brunoRoot);
        File.WriteAllText(Path.Combine(brunoRoot, "bruno.json"), """{"name":"test","version":"1"}""");

        try
        {
            var sut = new RequestTabViewModel(
                new TransportRegistry(),
                Substitute.For<ICollectionService>(),
                WeakReferenceMessenger.Default,
                _ => { });
            sut.CollectionRootPath = brunoRoot;

            var request = new CollectionRequest
            {
                FilePath = Path.Combine(brunoRoot, "req.bru"),
                Name = "get user",
                Method = HttpMethod.Get,
                Url = "https://api.example.com/users/:userId/orders/:orderId",
                PathParams = new Dictionary<string, string>
                {
                    ["userId"] = "1",
                    ["orderId"] = "2",
                },
            };

            sut.LoadRequest(request);

            sut.IsBrunoCollection.Should().BeTrue();
            sut.PathParamHintText.Should().Contain(":variable");
            sut.PathParams.Items.Should().HaveCount(2);
            sut.PathParams.Items.Should().Contain(item => item.Key == "userId" && item.Value == "1");
            sut.PathParams.Items.Should().Contain(item => item.Key == "orderId" && item.Value == "2");
        }
        finally
        {
            Directory.Delete(brunoRoot, recursive: true);
        }
    }

    [Fact]
    public void NonBrunoCollection_PathParamHintText_UsesBraceSyntax()
    {
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            WeakReferenceMessenger.Default,
            _ => { });

        // No CollectionRootPath set / not a Bruno collection
        sut.IsBrunoCollection.Should().BeFalse();
        sut.PathParamHintText.Should().Contain("{variable}");
    }

    [Fact]
    public void NonBrunoCollection_PathParamHintText_AlsoMentionsColonSyntax()
    {
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            WeakReferenceMessenger.Default,
            _ => { });

        sut.IsBrunoCollection.Should().BeFalse();
        sut.PathParamHintText.Should().Contain(":variable");
    }

    [Fact]
    public void BrunoCollection_PathParamHintText_DoesNotMentionBraceSyntax()
    {
        var brunoRoot = Path.Combine(Path.GetTempPath(), "BrunoHintTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(brunoRoot);
        File.WriteAllText(Path.Combine(brunoRoot, "bruno.json"), """{"name":"test","version":"1"}""");
        try
        {
            var sut = new RequestTabViewModel(
                new TransportRegistry(),
                Substitute.For<ICollectionService>(),
                WeakReferenceMessenger.Default,
                _ => { });
            sut.CollectionRootPath = brunoRoot;

            sut.IsBrunoCollection.Should().BeTrue();
            sut.PathParamHintText.Should().Contain(":variable");
            sut.PathParamHintText.Should().NotContain("{variable}");
        }
        finally
        {
            Directory.Delete(brunoRoot, recursive: true);
        }
    }

    // ── Callsmith: colon-syntax detection ────────────────────────────────────

    [Fact]
    public void CallsmithCollection_ColonSyntaxUrl_DetectsPathParams()
    {
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            WeakReferenceMessenger.Default,
            _ => { });

        var request = new CollectionRequest
        {
            FilePath = "c:/tmp/sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/users/:userId/orders/:orderId",
            PathParams = new Dictionary<string, string>
            {
                ["userId"] = "10",
                ["orderId"] = "20",
            },
        };

        sut.LoadRequest(request);

        sut.IsBrunoCollection.Should().BeFalse();
        sut.PathParams.Items.Should().HaveCount(2);
        sut.PathParams.Items.Should().Contain(item => item.Key == "userId" && item.Value == "10");
        sut.PathParams.Items.Should().Contain(item => item.Key == "orderId" && item.Value == "20");
    }

    [Fact]
    public void CallsmithCollection_MixedSyntaxUrl_DetectsPathParamsInOrder()
    {
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            WeakReferenceMessenger.Default,
            _ => { });

        var request = new CollectionRequest
        {
            FilePath = "c:/tmp/sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/users/{userId}/orders/:orderId",
            PathParams = new Dictionary<string, string>
            {
                ["userId"] = "10",
                ["orderId"] = "20",
            },
        };

        sut.LoadRequest(request);

        sut.PathParams.Items.Should().HaveCount(2);
        sut.PathParams.Items[0].Key.Should().Be("userId");
        sut.PathParams.Items[1].Key.Should().Be("orderId");
        sut.PathParams.Items[0].Value.Should().Be("10");
        sut.PathParams.Items[1].Value.Should().Be("20");
    }

    // ── Callsmith: key rename with colon / mixed syntax ───────────────────────

    [Fact]
    public void RenamingPathParamKey_ColonForm_UpdatesColonPlaceholder()
    {
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            WeakReferenceMessenger.Default,
            _ => { });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/users/:id",
            PathParams = new Dictionary<string, string> { ["id"] = "42" },
        });

        sut.PathParams.Items.Should().HaveCount(1);
        sut.PathParams.Items[0].Key = "userId";

        sut.Url.Should().Be("https://api.example.com/users/:userId");
        sut.PathParams.Items[0].Value.Should().Be("42");
    }

    [Fact]
    public void RenamingPathParamKey_MixedUrl_PreservesEachSyntaxForm()
    {
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            WeakReferenceMessenger.Default,
            _ => { });

        // URL has brace form first, colon form second.
        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/users/{id}/orders/:orderId",
            PathParams = new Dictionary<string, string>
            {
                ["id"] = "1",
                ["orderId"] = "2",
            },
        });

        sut.PathParams.Items.Should().HaveCount(2);

        // Rename the brace-form param — URL should keep brace form.
        sut.PathParams.Items[0].Key = "userId";
        sut.Url.Should().Be("https://api.example.com/users/{userId}/orders/:orderId");

        // Rename the colon-form param — URL should keep colon form.
        sut.PathParams.Items[1].Key = "orderRef";
        sut.Url.Should().Be("https://api.example.com/users/{userId}/orders/:orderRef");
    }
}
