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

public sealed class RequestEditorViewModelCloseWithoutSavingTests
{
    private static (RequestEditorViewModel Sut, ICollectionService CollectionService) BuildSut()
    {
        var messenger = new WeakReferenceMessenger();
        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RequestFileExtension.Returns(".callsmith");
        collectionService.SaveRequestAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var transportRegistry = Substitute.For<ITransportRegistry>();
        var dynamicEvaluator = Substitute.For<IDynamicVariableEvaluator>();

        var sut = new RequestEditorViewModel(
            transportRegistry,
            collectionService,
            preferencesService,
            dynamicEvaluator,
            new EnvironmentMergeService(dynamicEvaluator),
            messenger,
            NullLogger<RequestEditorViewModel>.Instance);

        return (sut, collectionService);
    }

    [Fact]
    public void CloseDirtyInactiveTab_ShowsGlobalCloseWithoutSavingDialog_WithRequestName()
    {
        var (sut, _) = BuildSut();
        sut.NewTab();
        sut.NewTab();

        var dirtyTab = sut.Tabs[0];
        dirtyTab.RequestName = "Create Account";
        dirtyTab.IsNew = false;
        dirtyTab.HasUnsavedChanges = true;

        sut.CloseTab(dirtyTab);

        sut.ShowCloseWithoutSavingDialog.Should().BeTrue();
        sut.CloseWithoutSavingRequestName.Should().Be("Create Account");
        sut.Tabs.Should().HaveCount(2);
    }

    [Fact]
    public void ConfirmCloseWithoutSaving_ClosesPendingTab_AndDismissesDialog()
    {
        var (sut, _) = BuildSut();
        sut.NewTab();
        sut.NewTab();

        var dirtyTab = sut.Tabs[0];
        dirtyTab.RequestName = "Create Account";
        dirtyTab.IsNew = false;
        dirtyTab.HasUnsavedChanges = true;

        sut.CloseTab(dirtyTab);
        sut.ConfirmCloseWithoutSavingCommand.Execute(null);

        sut.ShowCloseWithoutSavingDialog.Should().BeFalse();
        sut.CloseWithoutSavingRequestName.Should().BeEmpty();
        sut.Tabs.Should().HaveCount(1);
        sut.Tabs.Should().NotContain(dirtyTab);
    }

    [Fact]
    public void CancelCloseWithoutSaving_KeepsTabOpen_AndDismissesDialog()
    {
        var (sut, _) = BuildSut();
        sut.NewTab();
        sut.NewTab();

        var dirtyTab = sut.Tabs[0];
        dirtyTab.RequestName = "Create Account";
        dirtyTab.IsNew = false;
        dirtyTab.HasUnsavedChanges = true;

        sut.CloseTab(dirtyTab);
        sut.CancelCloseWithoutSavingCommand.Execute(null);

        sut.ShowCloseWithoutSavingDialog.Should().BeFalse();
        sut.CloseWithoutSavingRequestName.Should().BeEmpty();
        sut.Tabs.Should().HaveCount(2);
        sut.Tabs.Should().Contain(dirtyTab);
    }

    [Fact]
    public async Task SaveAndClosePendingTab_SavesAndClosesTab_AndDismissesDialog()
    {
        var (sut, collectionService) = BuildSut();
        var request = new CollectionRequest
        {
            FilePath = @"c:\tmp\requests\Create Account.callsmith",
            Name = "Create Account",
            Method = HttpMethod.Post,
            Url = "https://api.example.com/accounts",
            Headers = [],
            PathParams = new Dictionary<string, string>(),
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            Auth = new AuthConfig(),
        };

        sut.Receive(new RequestSelectedMessage(request));
        sut.NewTab();

        var dirtyTab = sut.Tabs[0];
        dirtyTab.Url = "https://api.example.com/accounts?force=true";

        sut.CloseTab(dirtyTab);
        await sut.SaveAndClosePendingTabCommand.ExecuteAsync(null);

        await collectionService.Received(1)
            .SaveRequestAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>());
        sut.ShowCloseWithoutSavingDialog.Should().BeFalse();
        sut.CloseWithoutSavingRequestName.Should().BeEmpty();
        sut.Tabs.Should().HaveCount(1);
        sut.Tabs.Should().NotContain(dirtyTab);
    }
}
