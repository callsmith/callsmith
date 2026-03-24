using System.Linq;
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

    [Fact]
    public void UpdateSourceRequest_WhenPathChanges_DoesNotMarkDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest(url: "https://api.example.com/auth/login"));
        sut.HasUnsavedChanges.Should().BeFalse();

        var renamed = new CollectionRequest
        {
            FilePath = @"c:\tmp\renamed-folder\sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/auth/login",
            QueryParams = [],
            PathParams = new Dictionary<string, string>(),
            Headers = [],
        };

        sut.UpdateSourceRequest(renamed);

        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void LoadingHistoryResponse_DoesNotMarkTabDirty()
    {
        // When a history response is loaded (IsResponseFromHistory is set),
        // the request should NOT be marked dirty since this is display state, not config.
        var mockHistoryService = Substitute.For<IHistoryService>();
        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            null,
            mockHistoryService);

        sut.LoadRequest(SampleRequest());
        sut.HasUnsavedChanges.Should().BeFalse();

        // Simulate what HydrateResponseFromHistoryAsync does
        sut.Response = new ResponseModel
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Body = "response body",
            Headers = new Dictionary<string, string>(),
            BodyBytes = System.Text.Encoding.UTF8.GetBytes("response body"),
            FinalUrl = "https://api.example.com/users",
            Elapsed = TimeSpan.FromMilliseconds(100),
        };
        sut.IsResponseFromHistory = true;
        sut.HistoryResponseDate = DateTimeOffset.UtcNow;

        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task LoadRequest_LoadsMostRecentResponseForSelectedEnvironment()
    {
        var requestId = Guid.NewGuid();
        var historyService = Substitute.For<IHistoryService>();
        var expected = new HistoryEntry
        {
            RequestId = requestId,
            SentAt = new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero),
            EnvironmentName = "dev",
            ConfiguredSnapshot = new ConfiguredRequestSnapshot
            {
                Method = "GET",
                Url = "https://api.example.com/users",
            },
            ResponseSnapshot = new ResponseSnapshot
            {
                StatusCode = 201,
                ReasonPhrase = "Created",
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Body = "{\"env\":\"dev\"}",
                FinalUrl = "https://api.example.com/dev",
                BodySizeBytes = 13,
                ElapsedMs = 42,
            },
        };

        historyService
            .GetLatestForRequestInEnvironmentAsync(requestId, "dev", Arg.Any<CancellationToken>())
            .Returns(expected);

        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            null,
            historyService);

        sut.SetEnvironment(new EnvironmentModel { FilePath = "dev.env.callsmith", Name = "dev", Variables = [], EnvironmentId = Guid.NewGuid() });

        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/request.callsmith",
            RequestId = requestId,
            Name = "request",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/users",
            QueryParams = [],
            PathParams = new Dictionary<string, string>(),
            Headers = [],
        });

        await AssertEventuallyAsync(() => sut.IsResponseFromHistory);

        sut.Response.Should().NotBeNull();
        sut.Response!.StatusCode.Should().Be(201);
        sut.Response.Body.Should().Be("{\"env\":\"dev\"}");

        await historyService.Received(1)
            .GetLatestForRequestInEnvironmentAsync(requestId, "dev", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetEnvironment_ReloadsHistoryForNewEnvironment_AndClearsWhenNoneExists()
    {
        var requestId = Guid.NewGuid();
        var historyService = Substitute.For<IHistoryService>();
        var prodLookupRequested = false;
        historyService
            .GetLatestForRequestInEnvironmentAsync(requestId, "dev", Arg.Any<CancellationToken>())
            .Returns(new HistoryEntry
            {
                RequestId = requestId,
                SentAt = new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero),
                EnvironmentName = "dev",
                ConfiguredSnapshot = new ConfiguredRequestSnapshot
                {
                    Method = "GET",
                    Url = "https://api.example.com/users",
                },
                ResponseSnapshot = new ResponseSnapshot
                {
                    StatusCode = 200,
                    ReasonPhrase = "OK",
                    Headers = new Dictionary<string, string>(),
                    Body = "dev-response",
                    FinalUrl = "https://api.example.com/dev",
                    BodySizeBytes = 12,
                    ElapsedMs = 5,
                },
            });
        historyService
            .GetLatestForRequestInEnvironmentAsync(requestId, "prod", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                prodLookupRequested = true;
                return (HistoryEntry?)null;
            });

        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            null,
            historyService);

        sut.SetEnvironment(new EnvironmentModel { FilePath = "dev.env.callsmith", Name = "dev", Variables = [], EnvironmentId = Guid.NewGuid() });
        sut.LoadRequest(new CollectionRequest
        {
            FilePath = "c:/tmp/request.callsmith",
            RequestId = requestId,
            Name = "request",
            Method = HttpMethod.Get,
            Url = "https://api.example.com/users",
            QueryParams = [],
            PathParams = new Dictionary<string, string>(),
            Headers = [],
        });

        await AssertEventuallyAsync(() => string.Equals(sut.Response?.Body, "dev-response", StringComparison.Ordinal));

        sut.SetEnvironment(new EnvironmentModel { FilePath = "prod.env.callsmith", Name = "prod", Variables = [], EnvironmentId = Guid.NewGuid() });

        await AssertEventuallyAsync(() => prodLookupRequested);
        await AssertEventuallyAsync(() => sut.Response is null && !sut.IsResponseFromHistory);

        await historyService.Received(1)
            .GetLatestForRequestInEnvironmentAsync(requestId, "prod", Arg.Any<CancellationToken>());
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            // Pump queued Dispatcher work so ViewModel callbacks posted to UI thread run in tests.
            if (Dispatcher.UIThread.CheckAccess())
                Dispatcher.UIThread.RunJobs();
            else
                await Dispatcher.UIThread.InvokeAsync(static () => Dispatcher.UIThread.RunJobs());

            if (condition())
                return;

            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }
}
