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

    [Fact]
    public void CloseAllTabs_PromptsDirtyTabsOneByOne_AndSwitchesToEachDirtyTab()
    {
        var (sut, _) = BuildSut();
        sut.NewTab();
        sut.NewTab();
        sut.NewTab();

        var firstDirty = sut.Tabs[0];
        firstDirty.RequestName = "First";
        firstDirty.IsNew = false;
        firstDirty.HasUnsavedChanges = true;

        var clean = sut.Tabs[1];
        clean.RequestName = "Clean";
        clean.IsNew = false;
        clean.HasUnsavedChanges = false;

        var secondDirty = sut.Tabs[2];
        secondDirty.RequestName = "Second";
        secondDirty.IsNew = false;
        secondDirty.HasUnsavedChanges = true;

        sut.CloseAllTabs();

        sut.ShowCloseWithoutSavingDialog.Should().BeTrue();
        sut.ActiveTab.Should().Be(firstDirty);
        sut.CloseWithoutSavingRequestName.Should().Be("First");

        sut.ConfirmCloseWithoutSavingCommand.Execute(null);

        sut.ShowCloseWithoutSavingDialog.Should().BeTrue();
        sut.ActiveTab.Should().Be(secondDirty);
        sut.CloseWithoutSavingRequestName.Should().Be("Second");
        sut.Tabs.Should().ContainSingle().Which.Should().Be(secondDirty);
    }

    [Fact]
    public void CloseOtherTabs_PromptsDirtyTabsOneByOne_ThenReturnsFocusToKeepTab()
    {
        var (sut, _) = BuildSut();
        sut.NewTab();
        sut.NewTab();
        sut.NewTab();

        var leftDirty = sut.Tabs[0];
        leftDirty.RequestName = "Left";
        leftDirty.IsNew = false;
        leftDirty.HasUnsavedChanges = true;

        var keep = sut.Tabs[1];
        keep.RequestName = "Keep";
        keep.IsNew = false;
        keep.HasUnsavedChanges = false;

        var rightDirty = sut.Tabs[2];
        rightDirty.RequestName = "Right";
        rightDirty.IsNew = false;
        rightDirty.HasUnsavedChanges = true;

        sut.ActiveTab = keep;

        sut.CloseOtherTabs(keep);

        sut.ShowCloseWithoutSavingDialog.Should().BeTrue();
        sut.ActiveTab.Should().Be(leftDirty);
        sut.CloseWithoutSavingRequestName.Should().Be("Left");

        sut.ConfirmCloseWithoutSavingCommand.Execute(null);

        sut.ShowCloseWithoutSavingDialog.Should().BeTrue();
        sut.ActiveTab.Should().Be(rightDirty);
        sut.CloseWithoutSavingRequestName.Should().Be("Right");

        sut.ConfirmCloseWithoutSavingCommand.Execute(null);

        sut.ShowCloseWithoutSavingDialog.Should().BeFalse();
        sut.Tabs.Should().ContainSingle().Which.Should().Be(keep);
        sut.ActiveTab.Should().Be(keep);
    }
}
