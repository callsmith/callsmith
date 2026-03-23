using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
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
        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>(
            [
                new HistoryEnvironmentOption { Name = "Zulu" },
                new HistoryEnvironmentOption { Name = "Dev" },
                new HistoryEnvironmentOption { Name = "Prod" },
                new HistoryEnvironmentOption { Name = "Archived" },
            ]));
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>(([], 0)));

        var environmentService = Substitute.For<IEnvironmentService>();
        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var messenger = Substitute.For<IMessenger>();
        var logger = Substitute.For<ILogger<EnvironmentViewModel>>();

        var environmentViewModel = new EnvironmentViewModel(environmentService, preferencesService, messenger, logger)
        {
            Environments =
            [
                new EnvironmentModel { Name = "Prod", FilePath = "prod.bru", Variables = [] },
                new EnvironmentModel { Name = "Dev", FilePath = "dev.bru", Variables = [] },
            ],
        };

        var sut = new HistoryPanelViewModel(historyService, environmentViewModel);

        sut.OpenGlobal();

        await AssertEventuallyAsync(() => sut.EnvironmentOptions.Count > 1);

        sut.EnvironmentOptions.Select(option => option.Name).Should().ContainInOrder(
            "All environments",
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
        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>(
            [
                new HistoryEnvironmentOption { Name = "Dev", Color = "#00AAFF" },
                new HistoryEnvironmentOption { Name = "Prod", Color = "#00DD88" },
            ]));
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>(([], 0)));

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
        var historyService = Substitute.For<IHistoryService>();
        historyService.GetEnvironmentOptionsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HistoryEnvironmentOption>>(
            [
                new HistoryEnvironmentOption { Name = "Dev", Color = "#00AAFF" },
            ]));
        historyService.QueryAsync(Arg.Any<HistoryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(IReadOnlyList<HistoryEntry> Entries, long TotalCount)>(([], 0)));

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