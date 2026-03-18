using System.Linq;
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
/// Verifies that PerformSaveAsync correctly clears HasUnsavedChanges regardless of
/// which field was edited, and that dirty-tracking correctly marks changes after load.
/// </summary>
public sealed class RequestTabViewModelSaveTests
{
    private static RequestTabViewModel BuildSut(ICollectionService? collectionService = null)
    {
        // Only install the default "always succeed" stub when no custom service is supplied.
        // If the caller provides a pre-configured mock, don't override its setup.
        var cs = collectionService ?? Substitute.For<ICollectionService>();
        if (collectionService is null)
        {
            cs.SaveRequestAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);
        }

        return new RequestTabViewModel(
            new TransportRegistry(),
            cs,
            new WeakReferenceMessenger(),
            _ => { });
    }

    private static CollectionRequest SampleRequest(
        string url = "https://api.example.com/users",
        IReadOnlyList<RequestKv>? queryParams = null,
        Dictionary<string, string>? pathParams = null,
        IReadOnlyList<RequestKv>? headers = null) =>
        new()
        {
            FilePath = @"c:\tmp\sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = url,
            QueryParams = queryParams ?? [],
            PathParams = pathParams ?? [],
            Headers = headers ?? [],
        };

    // -------------------------------------------------------------------------
    // Dirty tracking: mark dirty after load
    // -------------------------------------------------------------------------

    [Fact]
    public void AfterLoad_HasUnsavedChanges_IsFalse()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void ChangingUrl_MarksTabDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.Url = "https://api.example.com/posts";
        sut.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void ChangingMethod_MarksTabDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.SelectedMethod = "POST";
        sut.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void ChangingBody_MarksTabDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.Body = "{\"key\":\"value\"}";
        sut.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void AddingQueryParam_MarksTabDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.QueryParams.Items.Add(new KeyValueItemViewModel(_ => { }) { Key = "page", Value = "1" });
        sut.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void RemovingQueryParam_MarksTabDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest(queryParams: [new RequestKv("include", "orders")]));
        sut.HasUnsavedChanges.Should().BeFalse();

        var item = sut.QueryParams.Items.First();
        sut.QueryParams.Items.Remove(item);

        sut.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void AddingHeader_MarksTabDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.Headers.Items.Add(new KeyValueItemViewModel(_ => { }) { Key = "X-Api-Key", Value = "secret" });
        sut.HasUnsavedChanges.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Save clears dirty state
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Save_AfterUrlChange_ClearsDirtyState()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.Url = "https://api.example.com/posts";
        sut.HasUnsavedChanges.Should().BeTrue();

        await sut.PerformSaveAsync();

        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Save_AfterMethodChange_ClearsDirtyState()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.SelectedMethod = "POST";

        await sut.PerformSaveAsync();

        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Save_AfterBodyChange_ClearsDirtyState()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.SelectedBodyType = CollectionRequest.BodyTypes.Json;
        sut.Body = "{\"key\":\"value\"}";

        await sut.PerformSaveAsync();

        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Save_AfterQueryParamRemoval_ClearsDirtyState()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest(queryParams: [new RequestKv("include", "orders")]));

        var item = sut.QueryParams.Items.First();
        sut.QueryParams.Items.Remove(item);
        sut.HasUnsavedChanges.Should().BeTrue();

        await sut.PerformSaveAsync();

        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Save_AfterQueryParamRemoval_SavesEmptyQueryParams()
    {
        CollectionRequest? saved = null;
        var collectionService = Substitute.For<ICollectionService>();
        collectionService
            .SaveRequestAsync(Arg.Do<CollectionRequest>(r => saved = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = BuildSut(collectionService);
        sut.LoadRequest(SampleRequest(queryParams: [new RequestKv("include", "orders")]));

        sut.QueryParams.Items.Remove(sut.QueryParams.Items.First());
        await sut.PerformSaveAsync();

        saved.Should().NotBeNull();
        saved!.QueryParams.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_AfterHeaderRemoval_ClearsDirtyState()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest(headers: [new RequestKv("Authorization", "Bearer tok")]));

        sut.Headers.Items.Remove(sut.Headers.Items.First());
        sut.HasUnsavedChanges.Should().BeTrue();

        await sut.PerformSaveAsync();

        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Save_AfterQueryParamAddition_SavesParam()
    {
        CollectionRequest? saved = null;
        var collectionService = Substitute.For<ICollectionService>();
        collectionService
            .SaveRequestAsync(Arg.Do<CollectionRequest>(r => saved = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = BuildSut(collectionService);
        sut.LoadRequest(SampleRequest());
        sut.QueryParams.Items.Add(new KeyValueItemViewModel(_ => { }) { Key = "page", Value = "2" });

        await sut.PerformSaveAsync();

        saved!.QueryParams.Should().Contain(p => p.Key == "page" && p.Value == "2");
        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Save_WhenCollectionServiceThrows_ShowsError_AndKeepsDirty()
    {
        var collectionService = Substitute.For<ICollectionService>();
        collectionService
            .SaveRequestAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk full")));

        var sut = BuildSut(collectionService);
        sut.LoadRequest(SampleRequest());
        sut.Url = "https://api.example.com/posts";

        await sut.PerformSaveAsync();

        sut.HasUnsavedChanges.Should().BeTrue();
        sut.ErrorMessage.Should().Contain("disk full");
    }

    // -------------------------------------------------------------------------
    // Second edit after save is tracked correctly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AfterSave_FurtherEdits_MarkDirtyAgain()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.Url = "https://api.example.com/v2";

        await sut.PerformSaveAsync();
        sut.HasUnsavedChanges.Should().BeFalse();

        sut.Url = "https://api.example.com/v3";
        sut.HasUnsavedChanges.Should().BeTrue();
    }
}
