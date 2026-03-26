using System.Net.Http;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class RequestTabViewModelCloseTests
{
    private sealed class CloseTracker
    {
        public int Count { get; set; }
    }

    private static RequestTabViewModel BuildNewTab(
        CloseTracker closeTracker,
        ICollectionService? collectionService = null)
    {
        closeTracker.Count = 0;

        var service = collectionService ?? Substitute.For<ICollectionService>();
        service.RequestFileExtension.Returns(".callsmith");
        if (collectionService is null)
        {
            service.SaveRequestAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }

        return new RequestTabViewModel(
            new TransportRegistry(),
            service,
            new WeakReferenceMessenger(),
            _ => closeTracker.Count++)
        {
            IsNew = true,
        };
    }

    [Fact]
    public void Close_NewUnsavedTab_ShowsCloseConfirmation()
    {
        var closeTracker = new CloseTracker();
        var sut = BuildNewTab(closeTracker);

        sut.CloseCommand.Execute(null);

        sut.PendingClose.Should().BeTrue();
        closeTracker.Count.Should().Be(0);
    }

    [Fact]
    public void CancelClose_NewUnsavedTab_DismissesConfirmation()
    {
        var closeTracker = new CloseTracker();
        var sut = BuildNewTab(closeTracker);
        sut.CloseCommand.Execute(null);

        sut.CancelCloseCommand.Execute(null);

        sut.PendingClose.Should().BeFalse();
        closeTracker.Count.Should().Be(0);
    }

    [Fact]
    public void DiscardAndClose_NewUnsavedTab_ClosesTab()
    {
        var closeTracker = new CloseTracker();
        var sut = BuildNewTab(closeTracker);
        sut.CloseCommand.Execute(null);

        sut.DiscardAndCloseCommand.Execute(null);

        sut.PendingClose.Should().BeFalse();
        closeTracker.Count.Should().Be(1);
    }

    [Fact]
    public async Task SaveAndClose_NewUnsavedTab_OpensSaveAs_ThenClosesAfterCommit()
    {
        var closeTracker = new CloseTracker();
        CollectionRequest? savedRequest = null;
        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RequestFileExtension.Returns(".callsmith");
        collectionService
            .SaveRequestAsync(Arg.Do<CollectionRequest>(request => savedRequest = request), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = BuildNewTab(closeTracker, collectionService);
        sut.SelectedMethod = HttpMethod.Post.Method;
        sut.Url = "https://api.example.com/users";
        sut.CollectionRootPath = @"c:\tmp\callsmith-tests";
        sut.SaveAsName = "Created Request";

        await sut.SaveAndCloseCommand.ExecuteAsync(null);

        sut.ShowSaveAsPanel.Should().BeTrue();
        sut.PendingClose.Should().BeFalse();
    closeTracker.Count.Should().Be(0);

        await sut.CommitSaveAsCommand.ExecuteAsync(null);

        sut.ShowSaveAsPanel.Should().BeFalse();
        sut.IsNew.Should().BeFalse();
    closeTracker.Count.Should().Be(1);
        savedRequest.Should().NotBeNull();
        savedRequest!.FilePath.Should().Be(@"c:\tmp\callsmith-tests\Created Request.callsmith");
        savedRequest.Method.Should().Be(HttpMethod.Post);
        savedRequest.Url.Should().Be("https://api.example.com/users");
    }
}