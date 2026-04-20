using System.Net.Http;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class RequestTabViewModelCurlPasteTests
{
    private static RequestTabViewModel BuildSut()
    {
        return new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { });
    }

    [Fact]
    public void TryApplyCurlCommand_ReplacesExistingRequestAndMarksDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(new CollectionRequest
        {
            FilePath = Path.Combine("tmp", "sample.callsmith"),
            Name = "sample",
            Method = HttpMethod.Get,
            Url = "https://old.example.com/users/{id}",
            Headers = [new RequestKv("X-Old", "1")],
            QueryParams = [new RequestKv("old", "value")],
            PathParams = new Dictionary<string, string> { ["id"] = "42" },
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = """{"old":true}""",
            FormParams = [new KeyValuePair<string, string>("x", "1")],
            Auth = new AuthConfig
            {
                AuthType = AuthConfig.AuthTypes.Bearer,
                Token = "old-token",
            },
            Description = "old",
        });

        var applied = sut.TryApplyCurlCommand("""
                                              curl -X POST "https://api.example.com/users/{userId}?page=2" \
                                                -H "Content-Type: application/json" \
                                                -H "X-New: yes" \
                                                -d '{"name":"alice"}'
                                              """);

        applied.Should().BeTrue();
        sut.SelectedMethod.Should().Be("POST");
        sut.Url.Should().Be("https://api.example.com/users/{userId}");
        sut.QueryParams.GetAllKv().Should().ContainSingle(p => p.Key == "page" && p.Value == "2");
        sut.PathParams.Items.Should().ContainSingle(p => p.Key == "userId" && p.Value == string.Empty);
        sut.Headers.GetAllKv().Should().Contain(p => p.Key == "X-New" && p.Value == "yes");
        sut.Headers.GetAllKv().Should().NotContain(p => p.Key == "X-Old");
        sut.SelectedBodyType.Should().Be(CollectionRequest.BodyTypes.Json);
        sut.Body.ReplaceLineEndings("\n").Should().Be("""
                             {
                               "name": "alice"
                             }
                             """.ReplaceLineEndings("\n").TrimEnd());
        sut.FormParams.Items.Should().BeEmpty();
        sut.AuthType.Should().Be(AuthConfig.AuthTypes.None);
        sut.Description.Should().BeNull();
        sut.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void TryApplyCurlCommand_ReturnsFalse_ForNonCurlText()
    {
        var sut = BuildSut();
        sut.LoadRequest(new CollectionRequest
        {
            FilePath = Path.Combine("tmp", "sample.callsmith"),
            Name = "sample",
            Method = HttpMethod.Get,
            Url = "https://old.example.com",
        });

        var applied = sut.TryApplyCurlCommand("https://api.example.com/not-a-curl");

        applied.Should().BeFalse();
        sut.Url.Should().Be("https://old.example.com");
        sut.HasUnsavedChanges.Should().BeFalse();
    }
}
