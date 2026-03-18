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
}
