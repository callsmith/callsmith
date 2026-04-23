using System.Net.Http;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for <see cref="FolderSettingsViewModel"/> and the
/// <see cref="CollectionsViewModel.OpenFolderSettingsCommand"/> integration.
/// </summary>
public sealed class FolderSettingsViewModelTests
{
    private const string FakeCollectionPath = @"C:\collections\my-api";

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static CollectionTreeItemViewModel MakeFolderNode(
        string folderPath = @"C:\collections\my-api\folder",
        AuthConfig? auth = null)
    {
        var folder = new CollectionFolder
        {
            Name = "folder",
            FolderPath = folderPath,
            Requests = [],
            SubFolders = [],
            Auth = auth ?? new AuthConfig(),
        };
        return CollectionTreeItemViewModel.FromFolder(folder);
    }

    private static CollectionsViewModel BuildCollectionsSut(ICollectionService? collectionService = null)
    {
        var cs = collectionService ?? Substitute.For<ICollectionService>();
        cs.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new CollectionFolder
          {
              Name = "root",
              FolderPath = FakeCollectionPath,
              Requests = [],
              SubFolders = [],
          });

        var recent = Substitute.For<IRecentCollectionsService>();
        recent.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        var prefs = Substitute.For<ICollectionPreferencesService>();
        prefs.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new CollectionPreferences { ExpandedFolderPaths = [] });

        return new CollectionsViewModel(
            cs,
            recent,
            Substitute.For<ICollectionImportService>(),
            prefs,
            Substitute.For<IHistoryService>(),
            new WeakReferenceMessenger(),
            NullLogger<CollectionsViewModel>.Instance);
    }

    // ─── FolderSettingsViewModel construction ────────────────────────────────

    [Fact]
    public void Constructor_LoadsAuthFromNode()
    {
        var node = MakeFolderNode(auth: new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = "tok",
        });
        var cs = Substitute.For<ICollectionService>();

        var sut = new FolderSettingsViewModel(node, cs);

        sut.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        sut.AuthToken.Should().Be("tok");
        sut.FolderName.Should().Be("folder");
    }

    [Fact]
    public void Constructor_DefaultsToInheritWhenNodeHasNoAuth()
    {
        var node = MakeFolderNode();
        var cs = Substitute.For<ICollectionService>();

        var sut = new FolderSettingsViewModel(node, cs);

        sut.AuthType.Should().Be(AuthConfig.AuthTypes.Inherit);
        sut.IsAuthInherit.Should().BeTrue();
        sut.IsAuthBearer.Should().BeFalse();
    }

    // ─── Visibility helpers ──────────────────────────────────────────────────

    [Theory]
    [InlineData(AuthConfig.AuthTypes.Inherit, true,  false, false, false)]
    [InlineData(AuthConfig.AuthTypes.None,    false, false, false, false)]
    [InlineData(AuthConfig.AuthTypes.Bearer,  false, true,  false, false)]
    [InlineData(AuthConfig.AuthTypes.Basic,   false, false, true,  false)]
    [InlineData(AuthConfig.AuthTypes.ApiKey,  false, false, false, true)]
    public void VisibilityProperties_ReflectAuthType(
        string authType,
        bool expectedInherit,
        bool expectedBearer,
        bool expectedBasic,
        bool expectedApiKey)
    {
        var node = MakeFolderNode();
        var cs = Substitute.For<ICollectionService>();
        var sut = new FolderSettingsViewModel(node, cs) { AuthType = authType };

        sut.IsAuthInherit.Should().Be(expectedInherit);
        sut.IsAuthBearer.Should().Be(expectedBearer);
        sut.IsAuthBasic.Should().Be(expectedBasic);
        sut.IsAuthApiKey.Should().Be(expectedApiKey);
    }

    // ─── SaveCommand ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_CallsServiceWithCorrectAuth()
    {
        var node = MakeFolderNode(@"C:\collections\my-api\folder");
        var cs = Substitute.For<ICollectionService>();
        cs.SaveFolderAuthAsync(Arg.Any<string>(), Arg.Any<AuthConfig>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var sut = new FolderSettingsViewModel(node, cs)
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            AuthToken = "my-token",
        };

        await sut.SaveCommand.ExecuteAsync(null);

        await cs.Received(1).SaveFolderAuthAsync(
            @"C:\collections\my-api\folder",
            Arg.Is<AuthConfig>(a =>
                a.AuthType == AuthConfig.AuthTypes.Bearer &&
                a.Token == "my-token"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_RaisesCloseRequested()
    {
        var node = MakeFolderNode(@"C:\collections\my-api\folder");
        var cs = Substitute.For<ICollectionService>();
        cs.SaveFolderAuthAsync(Arg.Any<string>(), Arg.Any<AuthConfig>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var sut = new FolderSettingsViewModel(node, cs);
        var closed = false;
        sut.CloseRequested += (_, _) => closed = true;

        await sut.SaveCommand.ExecuteAsync(null);

        closed.Should().BeTrue();
    }

    // ─── CancelCommand ───────────────────────────────────────────────────────

    [Fact]
    public void Cancel_RaisesCloseWithoutSaving()
    {
        var node = MakeFolderNode(@"C:\collections\my-api\folder");
        var cs = Substitute.For<ICollectionService>();

        var sut = new FolderSettingsViewModel(node, cs);
        var closed = false;
        sut.CloseRequested += (_, _) => closed = true;

        sut.CancelCommand.Execute(null);

        closed.Should().BeTrue();
        cs.DidNotReceive().SaveFolderAuthAsync(
            Arg.Any<string>(), Arg.Any<AuthConfig>(), Arg.Any<CancellationToken>());
    }

    // ─── CollectionsViewModel integration ────────────────────────────────────

    [Fact]
    public async Task OpenFolderSettings_SetsPendingFolderSettings()
    {
        var cs = Substitute.For<ICollectionService>();
        cs.LoadFolderAuthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new AuthConfig { AuthType = AuthConfig.AuthTypes.Inherit });
        var sut = BuildCollectionsSut(cs);

        var node = MakeFolderNode();
        await ((IAsyncRelayCommand<CollectionTreeItemViewModel>)sut.OpenFolderSettingsCommand).ExecuteAsync(node);

        sut.PendingFolderSettings.Should().NotBeNull();
        sut.PendingFolderSettings!.FolderName.Should().Be("folder");
    }

    [Fact]
    public async Task OnFolderSettingsDialogClosed_ClearsPendingAndUpdatesNodeAuth()
    {
        var cs = Substitute.For<ICollectionService>();
        var sut = BuildCollectionsSut(cs);

        var node = MakeFolderNode();
        sut.PendingFolderSettings = new FolderSettingsViewModel(node, cs);

        var dialog = sut.PendingFolderSettings!;
        dialog.AuthType = AuthConfig.AuthTypes.Bearer;
        dialog.AuthToken = "updated-token";

        sut.OnFolderSettingsDialogClosed(node);

        sut.PendingFolderSettings.Should().BeNull();
        node.FolderAuth.Should().NotBeNull();
        node.FolderAuth!.AuthType.Should().Be(AuthConfig.AuthTypes.Bearer);
        node.FolderAuth.Token.Should().Be("updated-token");
    }
}
