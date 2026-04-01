using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class HistoryPanelViewModelTests
{
    [Fact]
    public async Task OpenGlobal_SelectsFirstEntryFromResults()
    {
        var firstEntry = CreateEntry(new AuthConfig()) with
        {
            Id = 1,
            RequestName = "First",
        };
        var secondEntry = CreateEntry(new AuthConfig()) with
        {
            Id = 2,
            RequestName = "Second",
        };

        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>([]));
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>(([firstEntry, secondEntry], 2)));

        var sut = new HistoryPanelViewModel(historyService);

        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.SelectedEntry?.Entry.Id == firstEntry.Id);

        sut.SelectedEntry.Should().NotBeNull();
        sut.SelectedEntry!.Entry.Id.Should().Be(firstEntry.Id);
    }

    [Fact]
    public async Task OpenGlobal_OrdersEnvironmentOptions_ByCurrentOrderThenAlphabeticalUnmatched()
    {
        List<HistoryEntry> entries =
        [
            CreateEntry(new AuthConfig()) with { Id = 1, EnvironmentName = "Zulu", SentAt = new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero) },
            CreateEntry(new AuthConfig()) with { Id = 2, EnvironmentName = "Dev", SentAt = new DateTimeOffset(2026, 3, 23, 12, 1, 0, TimeSpan.Zero) },
            CreateEntry(new AuthConfig()) with { Id = 3, EnvironmentName = "Prod", SentAt = new DateTimeOffset(2026, 3, 23, 12, 2, 0, TimeSpan.Zero) },
            CreateEntry(new AuthConfig()) with { Id = 4, EnvironmentName = "Archived", SentAt = new DateTimeOffset(2026, 3, 23, 12, 3, 0, TimeSpan.Zero) },
        ];

        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, entries.Count)));

        var environmentService = Substitute.For<IEnvironmentService>();
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var messenger = Substitute.For<IMessenger>();
        var logger = Substitute.For<ILogger<EnvironmentViewModel>>();

        var environmentViewModel = new EnvironmentViewModel(environmentService, preferencesService, messenger, logger)
        {
            Environments =
            [
                new EnvironmentModel { Name = "Prod", FilePath = "prod.bru", Variables = [], EnvironmentId = Guid.NewGuid() },
                new EnvironmentModel { Name = "Dev", FilePath = "dev.bru", Variables = [], EnvironmentId = Guid.NewGuid() },
            ],
        };

        var sut = new HistoryPanelViewModel(historyService, environmentViewModel);

        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Count > 1);

        sut.EnvironmentOptions.Select(option => option.Name).Should().ContainInOrder(
            "All environments",
            "(no environment)",
            "Prod",
            "Dev",
            "Archived",
            "Zulu");
    }

    [Fact]
    public void SelectingEntry_WithAuthSecret_ShowsRevealButton()
    {
        var historyService = Substitute.For<IHistoryService>();
        var sut = new HistoryPanelViewModel(historyService);

        sut.SelectedEntry = new HistoryEntryRowViewModel(CreateEntry(new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "super-secret-token",
        }));

        sut.HasHiddenSecrets.Should().BeTrue();
    }

    [Fact]
    public void SelectingEntry_WithSecretVariableBinding_ShowsRevealButton()
    {
        var historyService = Substitute.For<IHistoryService>();
        var sut = new HistoryPanelViewModel(historyService);

        var entry = CreateEntry(new AuthConfig());
        entry = entry with
        {
            VariableBindings =
            [
                new VariableBinding("{{token}}", "ciphertext", true),
            ],
        };

        sut.SelectedEntry = new HistoryEntryRowViewModel(entry);

        sut.HasHiddenSecrets.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmClearHistory_UsesSelectedEnvironmentAndDaysFromPurgeDialog()
    {
        List<HistoryEntry> entries =
        [
            CreateEntry(new AuthConfig()) with { Id = 1, EnvironmentName = "Dev", EnvironmentColor = "#00AAFF" },
            CreateEntry(new AuthConfig()) with { Id = 2, EnvironmentName = "Prod", EnvironmentColor = "#00DD88" },
        ];

        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, entries.Count)));

        var sut = new HistoryPanelViewModel(historyService);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Count >= 3);

        sut.SelectedEnvironmentOption = sut.EnvironmentOptions.First(option => option.Name == "Prod");
        sut.OpenPurgeDialogCommand.Execute(null);

        sut.PurgeOlderThanDaysText = "45";
        sut.ConfirmPurgeDialogCommand.Execute(null);

        await sut.ConfirmClearHistoryCommand.ExecuteAsync(null);

        await historyService.Received(1).PurgeOlderThanAsync(
            Arg.Is<DateTimeOffset>(cutoff => cutoff <= DateTimeOffset.UtcNow.AddDays(-44) && cutoff >= DateTimeOffset.UtcNow.AddDays(-46)),
            "Prod",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmClearHistory_WhenScoped_PassesScopedRequestId()
    {
        var requestId = Guid.NewGuid();
        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>([]));
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>(([], 0)));

        var sut = new HistoryPanelViewModel(historyService);
        sut.OpenForRequest(requestId, "Sample");

        await AssertEventuallyAsync(() => sut.IsOpen);

        sut.OpenPurgeDialogCommand.Execute(null);
        sut.PurgeOlderThanDaysText = "90";
        sut.ConfirmPurgeDialogCommand.Execute(null);

        await sut.ConfirmClearHistoryCommand.ExecuteAsync(null);

        await historyService.Received(1).PurgeOlderThanAsync(
            Arg.Any<DateTimeOffset>(),
            null,
            requestId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmClearHistory_WhenAllTimeChecked_UsesPurgeAllForSelectedEnvironment()
    {
        List<HistoryEntry> entries =
        [
            CreateEntry(new AuthConfig()) with { Id = 1, EnvironmentName = "Dev", EnvironmentColor = "#00AAFF" },
        ];

        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, entries.Count)));

        var sut = new HistoryPanelViewModel(historyService);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Count >= 2);

        sut.SelectedEnvironmentOption = sut.EnvironmentOptions.First(option => option.Name == "Dev");
        sut.OpenPurgeDialogCommand.Execute(null);

        sut.IsPurgeAllTime = true;
        sut.PurgeOlderThanDaysText = "not-a-number";
        sut.ConfirmPurgeDialogCommand.Execute(null);

        await sut.ConfirmClearHistoryCommand.ExecuteAsync(null);

        await historyService.Received(1).PurgeAllAsync(
            "Dev",
            null,
            Arg.Any<CancellationToken>());
        await historyService.DidNotReceive().PurgeOlderThanAsync(
            Arg.Any<DateTimeOffset>(),
            Arg.Any<string?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCommand_PassesGlobalSearchText_ToQueryFilter()
    {
        HistoryFilter? captured = null;
        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>([]));
        historyService.QueryAsync(Arg.Do<HistoryFilter>(f => captured = f), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>(([], 0)));

        var sut = new HistoryPanelViewModel(historyService);
        sut.GlobalSearchText = "  my search  ";

        await sut.SearchCommand.ExecuteAsync(null);

        captured.Should().NotBeNull();
        captured!.GlobalSearch.Should().Be("my search");
    }

    [Fact]
    public void HasActiveAdvancedFilters_IsFalse_Initially()
    {
        var historyService = Substitute.For<IHistoryService>();
        var sut = new HistoryPanelViewModel(historyService);

        sut.HasActiveAdvancedFilters.Should().BeFalse();
        sut.ActiveAdvancedFilterCount.Should().Be(0);
    }

    [Fact]
    public void HasActiveAdvancedFilters_IsTrue_WhenAdvancedSearchHasFilters()
    {
        var historyService = Substitute.For<IHistoryService>();
        var sut = new HistoryPanelViewModel(historyService);

        sut.AdvancedSearch.RequestContains = "token";

        sut.HasActiveAdvancedFilters.Should().BeTrue();
        sut.ActiveAdvancedFilterCount.Should().Be(1);
    }

    [Fact]
    public async Task OpenGlobal_LoadsInitialChunk_AndTracksRemainingResults()
    {
        var entries = Enumerable.Range(1, 100).Select(i => CreateEntry(new AuthConfig()) with { Id = i, RequestName = $"Request {i}" }).ToList();
        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>([]));
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, 250)));

        var sut = new HistoryPanelViewModel(historyService);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.Entries.Count == 100);

        sut.Entries.Should().HaveCount(100);
        sut.TotalCount.Should().Be(250);
        sut.HasMoreEntries.Should().BeTrue();
        sut.ResultCountLabel.Should().Be("Showing 100 of 250 results");
    }

    [Fact]
    public async Task EnsureMoreEntriesAsync_AppendsNextChunk()
    {
        var entries1 = Enumerable.Range(1, 100).Select(i => CreateEntry(new AuthConfig()) with { Id = i }).ToList();
        var entries2 = Enumerable.Range(101, 100).Select(i => CreateEntry(new AuthConfig()) with { Id = i }).ToList();

        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>([]));

        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                var filter = x.Arg<HistoryFilter>();
                if (filter.Page == 0)
                    return Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries1, 200));
                else
                    return Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries2, 200));
            });

        var sut = new HistoryPanelViewModel(historyService);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.Entries.Count == 100);
        sut.HasMoreEntries.Should().BeTrue();

        await sut.EnsureMoreEntriesAsync();
        await AssertEventuallyAsync(() => sut.Entries.Count == 200);

        sut.Entries.Should().HaveCount(200);
        sut.HasMoreEntries.Should().BeFalse();
        sut.HistoryListStatusMessage.Should().Be("You've reached the end of history.");
    }

    [Fact]
    public async Task EnsureMoreEntriesAsync_WhenIncrementalLoadIsInFlight_DoesNotStartAnotherQuery()
    {
        var entries = Enumerable.Range(1, 100).Select(i => CreateEntry(new AuthConfig()) with { Id = i }).ToList();

        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>([]));

        var tcs = new TaskCompletionSource<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>();
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                var filter = x.Arg<HistoryFilter>();
                if (filter.Page == 0)
                    return Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, 200));
                return tcs.Task;
            });

        var sut = new HistoryPanelViewModel(historyService);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.Entries.Count == 100 && sut.HasMoreEntries);

        var task1 = sut.EnsureMoreEntriesAsync();
        await Task.Delay(5);
        sut.IsIncrementalLoading.Should().BeTrue();

        var task2 = sut.EnsureMoreEntriesAsync();
        task2.IsCompleted.Should().BeTrue();

        tcs.SetResult(([], 200));
        await task1;

        await historyService.Received(2).QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenGlobal_EnvironmentOptions_AggregateById_UseCurrentNameAndColor_AndRowsKeepHistoricalValues()
    {
        var envId = Guid.NewGuid();
        List<HistoryEntry> entries =
        [
            CreateEntry(new AuthConfig()) with
            {
                Id = 1,
                EnvironmentId = envId,
                EnvironmentName = "env 1",
                EnvironmentColor = "#FF0000",
                SentAt = new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero),
            },
            CreateEntry(new AuthConfig()) with
            {
                Id = 2,
                EnvironmentId = envId,
                EnvironmentName = "env one",
                EnvironmentColor = "#0000FF",
                SentAt = new DateTimeOffset(2026, 3, 23, 12, 5, 0, TimeSpan.Zero),
            },
        ];

        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, entries.Count)));

        var environmentService = Substitute.For<IEnvironmentService>();
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var messenger = Substitute.For<IMessenger>();
        var logger = Substitute.For<ILogger<EnvironmentViewModel>>();

        var environmentViewModel = new EnvironmentViewModel(environmentService, preferencesService, messenger, logger)
        {
            Environments =
            [
                new EnvironmentModel
                {
                    Name = "env one",
                    FilePath = "env-one.bru",
                    Variables = [],
                    Color = "#0000FF",
                    EnvironmentId = envId,
                },
            ],
        };

        var sut = new HistoryPanelViewModel(historyService, environmentViewModel);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Any(o => o.Name == "env one"));

        sut.EnvironmentOptions.Count(o => o.Name == "env one").Should().Be(1);
        sut.EnvironmentOptions.First(o => o.Name == "env one").Color.Should().Be("#0000FF");

        sut.Entries.Select(e => e.Entry.EnvironmentName).Should().Contain(["env 1", "env one"]);
        sut.Entries.Select(e => e.Entry.EnvironmentColor).Should().Contain(["#FF0000", "#0000FF"]);
    }

    [Fact]
    public async Task Search_WhenEnvironmentOptionHasId_FiltersByEnvironmentId()
    {
        var envId = Guid.NewGuid();
        var capturedFilters = new List<HistoryFilter>();
        List<HistoryEntry> entries =
        [
            CreateEntry(new AuthConfig()) with
            {
                Id = 1,
                EnvironmentId = envId,
                EnvironmentName = "env 1",
                EnvironmentColor = "#FF0000",
            },
        ];

        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryAsync(Arg.Do<HistoryFilter>(filter => capturedFilters.Add(filter)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, entries.Count)));

        var environmentService = Substitute.For<IEnvironmentService>();
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var messenger = Substitute.For<IMessenger>();
        var logger = Substitute.For<ILogger<EnvironmentViewModel>>();

        var environmentViewModel = new EnvironmentViewModel(environmentService, preferencesService, messenger, logger)
        {
            Environments =
            [
                new EnvironmentModel
                {
                    Name = "env one",
                    FilePath = "env-one.bru",
                    Variables = [],
                    EnvironmentId = envId,
                },
            ],
        };

        var sut = new HistoryPanelViewModel(historyService, environmentViewModel);
        sut.OpenGlobal();
        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Any(o => o.Name == "env one"));

        sut.SelectedEnvironmentOption = sut.EnvironmentOptions.First(o => o.Name == "env one");
        await sut.SearchCommand.ExecuteAsync(null);

        capturedFilters.Should().NotBeEmpty();
        capturedFilters.Last().EnvironmentId.Should().Be(envId);
        capturedFilters.Last().EnvironmentName.Should().BeNull();
    }

    [Fact]
    public async Task OpenGlobal_AlwaysIncludesNoEnvironmentOption()
    {
        List<HistoryEntry> entries =
        [
            CreateEntry(new AuthConfig()) with { Id = 1, EnvironmentName = "Dev" },
            CreateEntry(new AuthConfig()) with { Id = 2, EnvironmentName = "Prod" },
        ];

        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, entries.Count)));

        var sut = new HistoryPanelViewModel(historyService);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Count >= 3);

        sut.EnvironmentOptions.Select(o => o.Name).Should().ContainInOrder(
            "All environments",
            "(no environment)");
    }

    [Fact]
    public async Task OpenGlobal_CurrentEnvironmentsAlwaysShown_EvenWithoutMatchingHistory()
    {
        List<HistoryEntry> entries =
        [
            CreateEntry(new AuthConfig()) with { Id = 1, EnvironmentName = "Dev" },
        ];

        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, entries.Count)));

        var environmentService = Substitute.For<IEnvironmentService>();
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var messenger = Substitute.For<IMessenger>();
        var logger = Substitute.For<ILogger<EnvironmentViewModel>>();

        var environmentViewModel = new EnvironmentViewModel(environmentService, preferencesService, messenger, logger)
        {
            Environments =
            [
                new EnvironmentModel { Name = "Staging", FilePath = "staging.bru", Variables = [], EnvironmentId = Guid.NewGuid() },
                new EnvironmentModel { Name = "Dev", FilePath = "dev.bru", Variables = [], EnvironmentId = Guid.NewGuid() },
            ],
        };

        var sut = new HistoryPanelViewModel(historyService, environmentViewModel);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Count >= 4);

        sut.EnvironmentOptions.Select(o => o.Name).Should().ContainInOrder(
            "All environments",
            "(no environment)",
            "Staging",
            "Dev");
    }

    [Fact]
    public async Task Search_WhenNoEnvironmentOptionSelected_SetsNoEnvironmentFilter()
    {
        List<HistoryEntry> entries =
        [
            CreateEntry(new AuthConfig()) with { Id = 1, EnvironmentName = null },
        ];

        var capturedFilters = new List<HistoryFilter>();
        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryAsync(Arg.Do<HistoryFilter>(f => capturedFilters.Add(f)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>((entries, entries.Count)));

        var sut = new HistoryPanelViewModel(historyService);
        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Any(o => o.Name == "(no environment)"));

        sut.SelectedEnvironmentOption = sut.EnvironmentOptions.First(o => o.Name == "(no environment)");
        await sut.SearchCommand.ExecuteAsync(null);

        capturedFilters.Should().NotBeEmpty();
        capturedFilters.Last().NoEnvironment.Should().BeTrue();
        capturedFilters.Last().EnvironmentId.Should().BeNull();
        capturedFilters.Last().EnvironmentName.Should().BeNull();
    }

    [Fact]
    public async Task SelectingEntry_WithDisabledHeaders_ExcludesThemFromConfiguredView()
    {
        var historyService = Substitute.For<IHistoryService>();
        var sut = new HistoryPanelViewModel(historyService);

        var entry = new HistoryEntry
        {
            Id = 1,
            Method = "GET",
            ResolvedUrl = "https://api.example.com/test",
            SentAt = DateTimeOffset.UtcNow,
            ElapsedMs = 10,
            ConfiguredSnapshot = new ConfiguredRequestSnapshot
            {
                Method = "GET",
                Url = "https://api.example.com/test",
                Headers =
                [
                    new RequestKv("X-Enabled", "yes", IsEnabled: true),
                    new RequestKv("X-Disabled", "no", IsEnabled: false),
                ],
                Auth = new AuthConfig(),
            },
            VariableBindings = [],
        };

        sut.SelectedEntry = new HistoryEntryRowViewModel(entry);

        await AssertEventuallyAsync(() => sut.DetailConfigured.Contains("X-Enabled"));
        sut.DetailConfigured.Should().NotContain("X-Disabled");
    }

    [Fact]
    public async Task ResendRequest_SendsMessageWithRevealedEntry_AndClosesPanel()
    {
        var originalEntry = CreateEntry(new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "{{secret}}",
        }) with
        {
            VariableBindings =
            [
                new VariableBinding("{{secret}}", null!, IsSecret: true, CiphertextValue: "cipher"),
            ],
        };

        var revealedEntry = originalEntry with
        {
            VariableBindings =
            [
                new VariableBinding("{{secret}}", "actual-token", IsSecret: true),
            ],
        };

        var historyService = Substitute.For<IHistoryService>();
        historyService.RevealSensitiveFieldsAsync(originalEntry, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(revealedEntry));

        ResendFromHistoryMessage? receivedMessage = null;
        var messenger = new WeakReferenceMessenger();
        messenger.Register<ResendFromHistoryMessage>(messenger, (_, msg) => receivedMessage = msg);

        var sut = new HistoryPanelViewModel(historyService, messenger: messenger);
        sut.OpenGlobal();
        sut.SelectedEntry = new HistoryEntryRowViewModel(originalEntry);

        await sut.ResendRequestCommand.ExecuteAsync(null);

        receivedMessage.Should().NotBeNull();
        receivedMessage!.Entry.Should().Be(revealedEntry);
        sut.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ResendRequest_DoesNothing_WhenNoEntrySelected()
    {
        var historyService = Substitute.For<IHistoryService>();
        var messenger = new WeakReferenceMessenger();

        ResendFromHistoryMessage? receivedMessage = null;
        messenger.Register<ResendFromHistoryMessage>(messenger, (_, msg) => receivedMessage = msg);

        var sut = new HistoryPanelViewModel(historyService, messenger: messenger);

        await sut.ResendRequestCommand.ExecuteAsync(null);

        receivedMessage.Should().BeNull();
        await historyService.DidNotReceive().RevealSensitiveFieldsAsync(Arg.Any<HistoryEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectingEntry_WithFormBody_ShowsFormParamsInConfiguredView()
    {
        var historyService = Substitute.For<IHistoryService>();
        var sut = new HistoryPanelViewModel(historyService);

        var entry = new HistoryEntry
        {
            Id = 1,
            Method = "POST",
            ResolvedUrl = "https://api.example.com/token",
            SentAt = DateTimeOffset.UtcNow,
            ElapsedMs = 10,
            ConfiguredSnapshot = new ConfiguredRequestSnapshot
            {
                Method = "POST",
                Url = "https://api.example.com/token",
                BodyType = CollectionRequest.BodyTypes.Form,
                FormParams =
                [
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", "{{my-client-id}}"),
                    new KeyValuePair<string, string>("client_secret", "{{my-secret-key}}"),
                ],
                Auth = new AuthConfig(),
            },
            VariableBindings = [],
        };

        sut.SelectedEntry = new HistoryEntryRowViewModel(entry);

        await AssertEventuallyAsync(() => sut.DetailConfigured.Contains("Body:"));
        sut.DetailConfigured.Should().Contain("grant_type=client_credentials&");
        sut.DetailConfigured.Should().Contain("client_id={{my-client-id}}&");
        sut.DetailConfigured.Should().Contain("client_secret={{my-secret-key}}");
        // Last param must NOT have trailing &
        sut.DetailConfigured.Should().NotContain("client_secret={{my-secret-key}}&");
    }

    [Fact]
    public async Task SelectingEntry_WithFormBody_ShowsFormParamsAsMultiLineInResolvedView()
    {
        var historyService = Substitute.For<IHistoryService>();
        var sut = new HistoryPanelViewModel(historyService);

        var entry = new HistoryEntry
        {
            Id = 1,
            Method = "POST",
            ResolvedUrl = "https://api.example.com/token",
            SentAt = DateTimeOffset.UtcNow,
            ElapsedMs = 10,
            ConfiguredSnapshot = new ConfiguredRequestSnapshot
            {
                Method = "POST",
                Url = "https://api.example.com/token",
                BodyType = CollectionRequest.BodyTypes.Form,
                FormParams =
                [
                    new KeyValuePair<string, string>("a", "b"),
                    new KeyValuePair<string, string>("c", "d"),
                    new KeyValuePair<string, string>("e", "f"),
                ],
                Auth = new AuthConfig(),
            },
            VariableBindings = [],
        };

        sut.SelectedEntry = new HistoryEntryRowViewModel(entry);

        await AssertEventuallyAsync(() => sut.DetailResolved.Contains("Body:"));
        sut.DetailResolved.Should().Contain("a=b&");
        sut.DetailResolved.Should().Contain("c=d&");
        sut.DetailResolved.Should().Contain("e=f");
        // Last param must NOT have trailing &
        sut.DetailResolved.Should().NotContain("e=f&");
        // Must NOT be a single joined line
        sut.DetailResolved.Should().NotContain("a=b&c=d");
    }

    [Fact]
    public async Task SelectingEntry_WithFormBodyAndSecretVariables_ShowsSecretPlaceholder_WhenNotRevealed()
    {
        var historyService = Substitute.For<IHistoryService>();
        var sut = new HistoryPanelViewModel(historyService);

        var entry = new HistoryEntry
        {
            Id = 1,
            Method = "POST",
            ResolvedUrl = "https://api.example.com/token",
            SentAt = DateTimeOffset.UtcNow,
            ElapsedMs = 10,
            ConfiguredSnapshot = new ConfiguredRequestSnapshot
            {
                Method = "POST",
                Url = "https://api.example.com/token",
                BodyType = CollectionRequest.BodyTypes.Form,
                FormParams =
                [
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", "{{my-client-id}}"),
                    new KeyValuePair<string, string>("client_secret", "{{my-secret-key}}"),
                ],
                Auth = new AuthConfig(),
            },
            VariableBindings =
            [
                new VariableBinding("{{my-client-id}}", "myAppId", IsSecret: true),
                new VariableBinding("{{my-secret-key}}", "s3cr3t", IsSecret: true),
            ],
        };

        // Secrets NOT revealed — default state
        sut.SelectedEntry = new HistoryEntryRowViewModel(entry);

        await AssertEventuallyAsync(() => sut.DetailResolved.Contains("Body:"));
        sut.DetailResolved.Should().Contain("grant_type=client_credentials&");
        sut.DetailResolved.Should().Contain("client_id=<secret>&");
        sut.DetailResolved.Should().Contain("client_secret=<secret>");
        // Must not contain URL-encoded braces or raw secret values
        sut.DetailResolved.Should().NotContain("%7B%7B");
        sut.DetailResolved.Should().NotContain("myAppId");
        sut.DetailResolved.Should().NotContain("s3cr3t");
    }

    [Fact]
    public async Task SelectingEntry_WithFormBodyAndSecretVariables_ShowsActualValues_WhenRevealed()
    {
        var entry = new HistoryEntry
        {
            Id = 1,
            Method = "POST",
            ResolvedUrl = "https://api.example.com/token",
            SentAt = DateTimeOffset.UtcNow,
            ElapsedMs = 10,
            ConfiguredSnapshot = new ConfiguredRequestSnapshot
            {
                Method = "POST",
                Url = "https://api.example.com/token",
                BodyType = CollectionRequest.BodyTypes.Form,
                FormParams =
                [
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", "{{my-client-id}}"),
                    new KeyValuePair<string, string>("client_secret", "{{my-secret-key}}"),
                ],
                Auth = new AuthConfig(),
            },
            VariableBindings =
            [
                new VariableBinding("{{my-client-id}}", "myAppId", IsSecret: true),
                new VariableBinding("{{my-secret-key}}", "s3cr3t", IsSecret: true),
            ],
        };

        var historyService = Substitute.For<IHistoryService>();
        historyService.RevealSensitiveFieldsAsync(Arg.Any<HistoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(entry));

        var sut = new HistoryPanelViewModel(historyService);
        sut.SelectedEntry = new HistoryEntryRowViewModel(entry);

        // Reveal secrets via command
        await sut.RevealSecretsCommand.ExecuteAsync(null);

        sut.DetailResolved.Should().Contain("Body:");
        sut.DetailResolved.Should().Contain("grant_type=client_credentials&");
        sut.DetailResolved.Should().Contain("client_id=myAppId&");
        sut.DetailResolved.Should().Contain("client_secret=s3cr3t");
        sut.DetailResolved.Should().NotContain("<secret>");
        // Last param must NOT have trailing &
        sut.DetailResolved.Should().NotContain("client_secret=s3cr3t&");
    }

    private static HistoryEntry CreateEntry(AuthConfig auth)
    {
        return new HistoryEntry
        {
            Id = 1,
            Method = "GET",
            ResolvedUrl = "https://api.example.com/test",
            SentAt = DateTimeOffset.UtcNow,
            ElapsedMs = 10,
            ConfiguredSnapshot = new ConfiguredRequestSnapshot
            {
                Method = "GET",
                Url = "https://api.example.com/test",
                Auth = auth,
            },
            VariableBindings = [],
        };
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }
}