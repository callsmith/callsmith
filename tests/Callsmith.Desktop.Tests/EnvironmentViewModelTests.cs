using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for <see cref="EnvironmentViewModel"/>.
/// </summary>
public sealed class EnvironmentViewModelTests
{
    private const string CollectionPath = @"C:\collections\my-api";

    private static EnvironmentViewModel BuildSut(
        IEnvironmentService service,
        ICollectionPreferencesService preferencesService,
        IMessenger messenger)
    {
        var logger = NullLogger<EnvironmentViewModel>.Instance;
        return new EnvironmentViewModel(service, preferencesService, messenger, logger);
    }

    private static EnvironmentModel MakeEnv(string name, string? filePath = null) =>
        new()
        {
            Name = name,
            FilePath = filePath ?? $@"C:\collections\my-api\environment\{name}.env.callsmith",
            Variables = [],
            EnvironmentId = Guid.NewGuid(),
        };

    /// <summary>
    /// Regression: renaming the active environment changes its file path.
    /// EnvironmentRenamedMessage must update ActiveEnvironment (and persist the new path)
    /// before the subsequent EnvironmentOrderChangedMessage reload fires, so the selection
    /// is retained after reload.
    /// </summary>
    [Fact]
    public async Task EnvironmentRenamed_WhenRenamedEnvIsActive_RetainsSelectionAndUpdatesPrefs()
    {
        var originalPath = @"C:\collections\my-api\environment\env 1.env.callsmith";
        var renamedPath  = @"C:\collections\my-api\environment\env one.env.callsmith";

        var originalEnv = MakeEnv("env 1", originalPath);
        var renamedEnv  = MakeEnv("env one", renamedPath);

        var service = Substitute.For<IEnvironmentService>();
        var prefsService = Substitute.For<ICollectionPreferencesService>();
        var messenger = new WeakReferenceMessenger();

        // First call (CollectionOpened): original env; second call (after rename reload): renamed env.
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns(
                   Task.FromResult<IReadOnlyList<EnvironmentModel>>([originalEnv]),
                   Task.FromResult<IReadOnlyList<EnvironmentModel>>([renamedEnv]));

        prefsService.LoadAsync(CollectionPath, Arg.Any<CancellationToken>())
                    .Returns(new CollectionPreferences());

        var persistedPaths = new List<string?>();
        prefsService.UpdateAsync(
                CollectionPath,
                Arg.Any<Func<CollectionPreferences, CollectionPreferences>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var updater = ci.Arg<Func<CollectionPreferences, CollectionPreferences>>();
                var updated = updater(new CollectionPreferences());
                persistedPaths.Add(updated.LastActiveEnvironmentFile);
                return Task.CompletedTask;
            });

        var sut = BuildSut(service, prefsService, messenger);

        // Open collection — loads original env.
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // User activates the original env in the toolbar.
        sut.ActiveEnvironment = originalEnv;
        await Task.Delay(50);

        // Editor completes the rename: sends EnvironmentRenamedMessage first…
        messenger.Send(new EnvironmentRenamedMessage(originalPath, renamedEnv));
        // …then EnvironmentOrderChangedMessage to trigger a reload.
        messenger.Send(new EnvironmentOrderChangedMessage(CollectionPath));
        await Task.Delay(100);

        // The active environment should point to the renamed model.
        sut.ActiveEnvironment.Should().NotBeNull("the renamed env should remain active");
        sut.ActiveEnvironment!.Name.Should().Be("env one");
        sut.ActiveEnvironment.FilePath.Should().Be(renamedPath);

        // Prefs must have been updated to the new relative path.
        var relativeRenamedPath = Path.GetRelativePath(CollectionPath, renamedPath);
        persistedPaths.Should().Contain(relativeRenamedPath,
            "preferences must be updated to the new path after a rename");
    }

    [Fact]
    public async Task EnvironmentRenamed_WhenRenamedEnvIsNotActive_DoesNotChangeActiveEnvironment()
    {
        var originalPath = @"C:\collections\my-api\environment\env 1.env.callsmith";
        var renamedPath  = @"C:\collections\my-api\environment\env one.env.callsmith";
        var otherPath    = @"C:\collections\my-api\environment\staging.env.callsmith";

        var originalEnv = MakeEnv("env 1", originalPath);
        var renamedEnv  = MakeEnv("env one", renamedPath);
        var otherEnv    = MakeEnv("staging", otherPath);

        var service = Substitute.For<IEnvironmentService>();
        var prefsService = Substitute.For<ICollectionPreferencesService>();
        var messenger = new WeakReferenceMessenger();

        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<EnvironmentModel>>([originalEnv, otherEnv]));

        prefsService.LoadAsync(CollectionPath, Arg.Any<CancellationToken>())
                    .Returns(new CollectionPreferences());
        prefsService.UpdateAsync(
                CollectionPath,
                Arg.Any<Func<CollectionPreferences, CollectionPreferences>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = BuildSut(service, prefsService, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Activate a different env (not the one being renamed).
        sut.ActiveEnvironment = otherEnv;

        messenger.Send(new EnvironmentRenamedMessage(originalPath, renamedEnv));

        sut.ActiveEnvironment.Should().NotBeNull();
        sut.ActiveEnvironment!.FilePath.Should().Be(otherPath, "unrelated rename must not affect the active env");
    }
}
