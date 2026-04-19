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
    public void AfterLoad_WithNonNoneBodyType_HasUnsavedChanges_IsFalse()
    {
        var req = new CollectionRequest
        {
            FilePath = @"c:\tmp\sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{\"key\":\"value\"}",
        };
        var sut = BuildSut();
        sut.LoadRequest(req);
        sut.HasUnsavedChanges.Should().BeFalse("loading a request with a body type should not mark it dirty");
    }

    [Fact]
    public void ChangingBodyTypeViaOption_MarksTabDirty()
    {
        // Ensures that the SelectedBodyTypeOption setter (the path used by the
        // code-behind SelectionChanged handler for genuine user clicks) does
        // mark the tab dirty when the selected type actually changes.
        var req = new CollectionRequest
        {
            FilePath = @"c:\tmp\sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{\"key\":\"value\"}",
        };
        var sut = BuildSut();
        sut.LoadRequest(req);

        var xmlOption = sut.BodyTypes.First(o => !o.IsSeparator && o.Value == CollectionRequest.BodyTypes.Xml);
        sut.SelectedBodyTypeOption = xmlOption;

        sut.HasUnsavedChanges.Should().BeTrue("switching body type via option should mark the tab dirty");
        sut.SelectedBodyType.Should().Be(CollectionRequest.BodyTypes.Xml);
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
    public void ChangingResponsePathFilterExpression_DoesNotMarkTabDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());

        sut.ResponsePathFilterExpression = "$.results[0].id";

        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void ResponsePathFilterExpression_IsTabScoped()
    {
        var firstTab = BuildSut();
        var secondTab = BuildSut();

        firstTab.ResponsePathFilterExpression = "$.results[0].id";

        secondTab.ResponsePathFilterExpression.Should().BeEmpty();
        firstTab.ResponsePathFilterExpression.Should().Be("$.results[0].id");
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
    // Body type switching: immediate content restore (no tab reload required)
    // -------------------------------------------------------------------------

    [Fact]
    public void SwitchingBodyType_RestoresPreservedContentImmediately()
    {
        // Simulates a request loaded from a .bru file that has both body:json and body:text blocks.
        var req = new CollectionRequest
        {
            FilePath = @"c:\tmp\sample.bru",
            Name = "sample",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{\"active\":true}",
            AllBodyContents = new Dictionary<string, string>
            {
                [CollectionRequest.BodyTypes.Json] = "{\"active\":true}",
                [CollectionRequest.BodyTypes.Text] = "some preserved text",
            },
        };

        var sut = BuildSut();
        sut.LoadRequest(req);

        // Active type is json; body shows json content.
        sut.SelectedBodyType.Should().Be(CollectionRequest.BodyTypes.Json);
        sut.Body.Should().Be("{\"active\":true}");

        // Switch to text: should immediately show the preserved text content.
        sut.SelectedBodyType = CollectionRequest.BodyTypes.Text;
        sut.Body.Should().Be("some preserved text");

        // Switch back to json: original json content restored without tab reload.
        sut.SelectedBodyType = CollectionRequest.BodyTypes.Json;
        sut.Body.Should().Be("{\"active\":true}");
    }

    [Fact]
    public void SwitchingBodyType_NoPreservedContent_ShowsEmpty()
    {
        // Request has only json body; switching to text shows empty (no preserved text).
        var req = new CollectionRequest
        {
            FilePath = @"c:\tmp\sample.bru",
            Name = "sample",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{\"a\":1}",
            AllBodyContents = new Dictionary<string, string>
            {
                [CollectionRequest.BodyTypes.Json] = "{\"a\":1}",
            },
        };

        var sut = BuildSut();
        sut.LoadRequest(req);

        sut.SelectedBodyType = CollectionRequest.BodyTypes.Text;
        sut.Body.Should().BeEmpty();
    }

    [Fact]
    public void EditingBody_AfterSwitch_StashesContentForEachType()
    {
        // When the user edits body content after switching types, each type's content
        // should be independently tracked.
        var req = new CollectionRequest
        {
            FilePath = @"c:\tmp\sample.bru",
            Name = "sample",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{\"original\":true}",
            AllBodyContents = new Dictionary<string, string>
            {
                [CollectionRequest.BodyTypes.Json] = "{\"original\":true}",
            },
        };

        var sut = BuildSut();
        sut.LoadRequest(req);

        // User edits the json body.
        sut.Body = "{\"edited\":true}";

        // Switch to text, type new text.
        sut.SelectedBodyType = CollectionRequest.BodyTypes.Text;
        sut.Body = "typed text";

        // Switch back to json: should show the edited json, not the original.
        sut.SelectedBodyType = CollectionRequest.BodyTypes.Json;
        sut.Body.Should().Be("{\"edited\":true}");

        // Switch back to text: should show the typed text.
        sut.SelectedBodyType = CollectionRequest.BodyTypes.Text;
        sut.Body.Should().Be("typed text");
    }

    [Fact]
    public async Task Save_AfterBodyTypeSwitch_PersistsAllBodyContents()
    {
        // When saving with the active body type as "text", AllBodyContents should include
        // the previously-edited json body so it can be round-tripped to disk.
        CollectionRequest? saved = null;
        var collectionService = Substitute.For<ICollectionService>();
        collectionService
            .SaveRequestAsync(Arg.Do<CollectionRequest>(r => saved = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var req = new CollectionRequest
        {
            FilePath = @"c:\tmp\sample.bru",
            Name = "sample",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{\"a\":1}",
            AllBodyContents = new Dictionary<string, string>
            {
                [CollectionRequest.BodyTypes.Json] = "{\"a\":1}",
            },
        };

        var sut = BuildSut(collectionService);
        sut.LoadRequest(req);

        // Edit json body, then switch to text and type something.
        sut.Body = "{\"b\":2}";
        sut.SelectedBodyType = CollectionRequest.BodyTypes.Text;
        sut.Body = "hello";

        await sut.PerformSaveAsync();

        saved.Should().NotBeNull();
        saved!.BodyType.Should().Be(CollectionRequest.BodyTypes.Text);
        saved.Body.Should().Be("hello");
        saved.AllBodyContents.Should().ContainKey(CollectionRequest.BodyTypes.Json)
            .WhoseValue.Should().Be("{\"b\":2}");
        saved.AllBodyContents.Should().ContainKey(CollectionRequest.BodyTypes.Text)
            .WhoseValue.Should().Be("hello");
    }

    [Fact]
    public void SwitchingToNone_ClearsBody_AndPreservesContentsForReturn()
    {
        var req = new CollectionRequest
        {
            FilePath = @"c:\tmp\sample.bru",
            Name = "sample",
            Method = HttpMethod.Post,
            Url = "https://api.example.com",
            BodyType = CollectionRequest.BodyTypes.Json,
            Body = "{\"x\":1}",
            AllBodyContents = new Dictionary<string, string>
            {
                [CollectionRequest.BodyTypes.Json] = "{\"x\":1}",
            },
        };

        var sut = BuildSut();
        sut.LoadRequest(req);

        // Switch to "none" — body editor disappears.
        sut.SelectedBodyType = CollectionRequest.BodyTypes.None;
        sut.Body.Should().BeEmpty();

        // Switch back to json — original content restored.
        sut.SelectedBodyType = CollectionRequest.BodyTypes.Json;
        sut.Body.Should().Be("{\"x\":1}");
    }



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

        var devEnvId = Guid.NewGuid();
        historyService
            .GetLatestForRequestInEnvironmentAsync(requestId, devEnvId, Arg.Any<CancellationToken>())
            .Returns(expected);

        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            null,
            historyService);

        sut.SetEnvironment(new EnvironmentModel { FilePath = "dev.env.callsmith", Name = "dev", Variables = [], EnvironmentId = devEnvId });

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
            .GetLatestForRequestInEnvironmentAsync(requestId, devEnvId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetEnvironment_ReloadsHistoryForNewEnvironment_AndClearsWhenNoneExists()
    {
        var requestId = Guid.NewGuid();
        var historyService = Substitute.For<IHistoryService>();
        var devEnvId = Guid.NewGuid();
        var prodEnvId = Guid.NewGuid();
        var prodLookupRequested = 0;
        historyService
            .GetLatestForRequestInEnvironmentAsync(requestId, devEnvId, Arg.Any<CancellationToken>())
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
            .GetLatestForRequestInEnvironmentAsync(requestId, prodEnvId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Exchange(ref prodLookupRequested, 1);
                return (HistoryEntry?)null;
            });

        var sut = new RequestTabViewModel(
            new TransportRegistry(),
            Substitute.For<ICollectionService>(),
            new WeakReferenceMessenger(),
            _ => { },
            null,
            historyService);

        sut.SetEnvironment(new EnvironmentModel { FilePath = "dev.env.callsmith", Name = "dev", Variables = [], EnvironmentId = devEnvId });
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

        sut.SetEnvironment(new EnvironmentModel { FilePath = "prod.env.callsmith", Name = "prod", Variables = [], EnvironmentId = prodEnvId });

        await AssertEventuallyAsync(() => Volatile.Read(ref prodLookupRequested) == 1);
        await AssertEventuallyAsync(() => sut.Response is null && !sut.IsResponseFromHistory);

        await historyService.Received(1)
            .GetLatestForRequestInEnvironmentAsync(requestId, prodEnvId, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Revert clears dirty state
    // -------------------------------------------------------------------------

    [Fact]
    public void Revert_AfterUrlChange_RestoresOriginalUrlAndClearsDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest(url: "https://api.example.com/users"));
        sut.Url = "https://api.example.com/posts";
        sut.HasUnsavedChanges.Should().BeTrue();

        sut.RevertCommand.Execute(null);

        sut.Url.Should().Be("https://api.example.com/users");
        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void Revert_AfterMethodChange_RestoresOriginalMethodAndClearsDirty()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.SelectedMethod = "POST";
        sut.HasUnsavedChanges.Should().BeTrue();

        sut.RevertCommand.Execute(null);

        sut.SelectedMethod.Should().Be("GET");
        sut.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void Revert_IsNotAvailableForNewTab()
    {
        var sut = BuildSut();
        // New tab — no request loaded, IsNew is true
        sut.RevertCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Revert_IsNotAvailableWhenTabIsClean()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.HasUnsavedChanges.Should().BeFalse();

        sut.RevertCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ShowRevertButton_IsFalseForNewTab()
    {
        var sut = BuildSut();
        sut.ShowRevertButton.Should().BeFalse();
    }

    [Fact]
    public void ShowRevertButton_IsFalseWhenClean()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.ShowRevertButton.Should().BeFalse();
    }

    [Fact]
    public void ShowRevertButton_IsTrueWhenExistingRequestHasUnsavedChanges()
    {
        var sut = BuildSut();
        sut.LoadRequest(SampleRequest());
        sut.Url = "https://api.example.com/posts";
        sut.ShowRevertButton.Should().BeTrue();
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            // Pump UI queue so Dispatcher.Post work from the ViewModel runs in headless tests.
            // CheckAccess returns true on UI threads; false on worker threads (especially on Linux).
            if (Dispatcher.UIThread.CheckAccess())
            {
                try
                {
                    Dispatcher.UIThread.RunJobs();
                }
                catch (InvalidOperationException)
                {
                    // Dispatcher processing can be briefly suspended; retry.
                }
            }
            else
            {
                // Non-UI thread: schedule a no-op to force at least one dispatcher cycle
                try
                {
                    var task = Dispatcher.UIThread.InvokeAsync(() => { });
                    // Don't await; just let it queue and yield control
                    _ = task;
                }
                catch
                {
                    // Dispatcher may be unavailable; continue
                }
            }

            if (condition())
                return;

            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }
}
