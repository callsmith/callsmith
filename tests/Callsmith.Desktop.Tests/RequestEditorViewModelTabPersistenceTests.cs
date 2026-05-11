using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Messages;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class RequestEditorViewModelTabPersistenceTests
{
    [Fact]
    public async Task PersistSessionAsync_WhenUnsavedTabsExist_PersistsDraftState()
    {
        var collectionPath = CreateTempCollectionPath();

        try
        {
            var sourcePath = Path.Combine(collectionPath, "saved.callsmith");
            await File.WriteAllTextAsync(sourcePath, "{}");

            var collectionService = Substitute.For<ICollectionService>();
            collectionService.RequestFileExtension.Returns(".callsmith");
            collectionService.OpenFolderAsync(collectionPath, Arg.Any<CancellationToken>())
                .Returns(new CollectionFolder
                {
                    FolderPath = collectionPath,
                    Name = "collection",
                    Requests = [],
                    SubFolders = []
                });

            var storedPreferences = new CollectionPreferences();
            var preferencesService = Substitute.For<ICollectionPreferencesService>();
            preferencesService.LoadAsync(collectionPath, Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(storedPreferences));
            preferencesService.UpdateAsync(collectionPath, Arg.Any<Func<CollectionPreferences, CollectionPreferences>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var update = ci.ArgAt<Func<CollectionPreferences, CollectionPreferences>>(1);
                    storedPreferences = update(storedPreferences);
                    return Task.CompletedTask;
                });

            var sut = BuildSut(collectionService, preferencesService);
            sut.Receive(new CollectionOpenedMessage(collectionPath));

            sut.NewTab();
            var newTab = sut.Tabs[0];
            newTab.RequestName = "Draft request";
            newTab.Url = "https://example.com/draft";
            newTab.SelectedBodyType = CollectionRequest.BodyTypes.Json;
            newTab.Body = "{\"draft\":true}";

            var savedRequest = CreateRequest(sourcePath, "Saved request", "https://example.com/original");
            sut.Receive(new RequestSelectedMessage(savedRequest, openAsPermanent: true));
            var dirtySavedTab = sut.Tabs[1];
            dirtySavedTab.Url = "https://example.com/edited";
            sut.ActiveTab = newTab;

            await sut.PersistSessionAsync();

            storedPreferences.OpenTabs.Should().HaveCount(2);
            storedPreferences.ActiveTabIndex.Should().Be(0);

            storedPreferences.OpenTabs![0].SourceFilePath.Should().BeNull();
            storedPreferences.OpenTabs[0].DraftRequest.Should().NotBeNull();
            storedPreferences.OpenTabs[0].DraftRequest!.Name.Should().Be("Draft request");
            storedPreferences.OpenTabs[0].DraftRequest!.Url.Should().Be("https://example.com/draft");
            storedPreferences.OpenTabs[0].DraftRequest!.Body.Should().Be("{\"draft\":true}");

            storedPreferences.OpenTabs[1].SourceFilePath.Should().Be("saved.callsmith");
            storedPreferences.OpenTabs[1].DraftRequest.Should().NotBeNull();
            storedPreferences.OpenTabs[1].DraftRequest!.Url.Should().Be("https://example.com/edited");
        }
        finally
        {
            TryDeleteDirectory(collectionPath);
        }
    }

    [Fact]
    public async Task ReceiveCollectionOpened_RestoresUnsavedTabsFromPreferences()
    {
        var collectionPath = CreateTempCollectionPath();

        try
        {
            var sourcePath = Path.Combine(collectionPath, "saved.callsmith");
            await File.WriteAllTextAsync(sourcePath, "{}");

            var collectionService = Substitute.For<ICollectionService>();
            collectionService.RequestFileExtension.Returns(".callsmith");
            collectionService.OpenFolderAsync(collectionPath, Arg.Any<CancellationToken>())
                .Returns(new CollectionFolder
                {
                    FolderPath = collectionPath,
                    Name = "collection",
                    Requests = [],
                    SubFolders = []
                });

            collectionService.LoadRequestAsync(sourcePath, Arg.Any<CancellationToken>())
                .Returns(CreateRequest(sourcePath, "Saved request", "https://example.com/original"));

            var preferencesService = Substitute.For<ICollectionPreferencesService>();
            preferencesService.LoadAsync(collectionPath, Arg.Any<CancellationToken>())
                .Returns(new CollectionPreferences
                {
                    OpenTabs =
                    [
                        new OpenRequestTabState
                        {
                            DraftRequest = new CollectionRequest
                            {
                                FilePath = string.Empty,
                                Name = "Draft request",
                                Method = HttpMethod.Post,
                                Url = "https://example.com/draft",
                                Headers = [],
                                PathParams = new Dictionary<string, string>(),
                                QueryParams = [],
                                BodyType = CollectionRequest.BodyTypes.Json,
                                Body = "{\"draft\":true}",
                                AllBodyContents = new Dictionary<string, string>
                                {
                                    [CollectionRequest.BodyTypes.Json] = "{\"draft\":true}"
                                },
                                Auth = new AuthConfig(),
                            }
                        },
                        new OpenRequestTabState
                        {
                            SourceFilePath = "saved.callsmith",
                            DraftRequest = CreateRequest(sourcePath, "Saved request", "https://example.com/edited")
                        }
                    ],
                    ActiveTabIndex = 1
                });

            var sut = BuildSut(collectionService, preferencesService);
            sut.Receive(new CollectionOpenedMessage(collectionPath));

            await WaitForConditionAsync(() => sut.Tabs.Count == 2);

            sut.Tabs[0].IsNew.Should().BeTrue();
            sut.Tabs[0].RequestName.Should().Be("Draft request");
            sut.Tabs[0].Url.Should().Be("https://example.com/draft");
            sut.Tabs[0].Body.Should().Be("{\"draft\":true}");

            sut.Tabs[1].IsNew.Should().BeFalse();
            sut.Tabs[1].HasUnsavedChanges.Should().BeTrue();
            sut.Tabs[1].SourceFilePath.Should().Be(sourcePath);
            sut.Tabs[1].Url.Should().Be("https://example.com/edited");
            sut.ActiveTab.Should().Be(sut.Tabs[1]);
        }
        finally
        {
            TryDeleteDirectory(collectionPath);
        }
    }

    private static RequestEditorViewModel BuildSut(
        ICollectionService collectionService,
        ICollectionPreferencesService preferencesService)
    {
        var messenger = new WeakReferenceMessenger();
        var transportRegistry = Substitute.For<ITransportRegistry>();
        var dynamicEvaluator = Substitute.For<IDynamicVariableEvaluator>();

        return new RequestEditorViewModel(
            transportRegistry,
            collectionService,
            preferencesService,
            dynamicEvaluator,
            new EnvironmentMergeService(dynamicEvaluator),
            messenger,
            NullLogger<RequestEditorViewModel>.Instance);
    }

    private static CollectionRequest CreateRequest(string filePath, string name, string url) =>
        new()
        {
            FilePath = filePath,
            Name = name,
            Method = HttpMethod.Get,
            Url = url,
            Headers = [],
            PathParams = new Dictionary<string, string>(),
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            Auth = new AuthConfig(),
        };

    private static string CreateTempCollectionPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "callsmith-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
                throw new TimeoutException("Condition was not met in time.");

            await Task.Delay(25);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
