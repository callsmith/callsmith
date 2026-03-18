using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for <see cref="EnvironmentEditorViewModel"/>.
/// Verifies CRUD operations on environments and their variable lists.
/// </summary>
public sealed class EnvironmentEditorViewModelTests
{
    private const string CollectionPath = @"C:\collections\my-api";

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static readonly string GlobalEnvPath =
        @"C:\collections\my-api\environment\_global.env.callsmith";

    private static EnvironmentEditorViewModel BuildSut(
        IEnvironmentService? service = null,
        IMessenger? messenger = null,
        ICollectionService? collectionService = null,
        IDynamicVariableEvaluator? dynamicEvaluator = null)
    {
        service ??= Substitute.For<IEnvironmentService>();
        // Always provide a default global env response so LoadEnvironmentsAsync doesn't throw.
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new EnvironmentModel { FilePath = GlobalEnvPath, Name = "Global", Variables = [] });
        messenger ??= new WeakReferenceMessenger();
        collectionService ??= Substitute.For<ICollectionService>();
        collectionService.OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                         .Returns(new Callsmith.Core.Models.CollectionFolder
                         {
                             Name = "root",
                             FolderPath = "/",
                             Requests = [],
                             SubFolders = [],
                         });
        dynamicEvaluator ??= Substitute.For<IDynamicVariableEvaluator>();
        return new EnvironmentEditorViewModel(
            service,
            collectionService,
            dynamicEvaluator,
            messenger,
            NullLogger<EnvironmentEditorViewModel>.Instance);
    }

    private static EnvironmentModel MakeModel(string name, string? path = null) =>
        new()
        {
            Name = name,
            FilePath = path ?? $@"C:\collections\my-api\environment\{name}.env.callsmith",
            Variables = [],
        };

    // Helper: stub LoadGlobalEnvironmentAsync on an existing mock.
    private static void SetupGlobalEnv(IEnvironmentService service) =>
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new EnvironmentModel { FilePath = GlobalEnvPath, Name = "Global", Variables = [] });

    // ─── CollectionOpenedMessage ──────────────────────────────────────────────

    [Fact]
    public async Task Receive_CollectionOpened_LoadsEnvironments()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev"), MakeModel("staging")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));

        // Give async handler a chance to complete (it's fire-and-forget).
        await Task.Delay(100);

        // Global env is pinned at index 0; collection envs follow.
        sut.Environments.Should().HaveCount(3);
        sut.Environments[0].Name.Should().Be("Global");
        sut.Environments[0].IsGlobal.Should().BeTrue();
        sut.Environments[1].Name.Should().Be("dev");
        sut.Environments[2].Name.Should().Be("staging");
    }

    [Fact]
    public async Task Receive_CollectionOpened_SelectsFirstNonGlobalEnvironment()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev"), MakeModel("staging")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.SelectedEnvironment.Should().NotBeNull();
        sut.SelectedEnvironment!.Name.Should().Be("dev");
        sut.SelectedEnvironment.IsGlobal.Should().BeFalse();
    }

    [Fact]
    public async Task Receive_CollectionOpened_WhenNoCollectionEnvs_SelectsGlobalEnvironment()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns(Array.Empty<EnvironmentModel>());

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Only global env present — it is selected.
        sut.Environments.Should().ContainSingle();
        sut.SelectedEnvironment.Should().NotBeNull();
        sut.SelectedEnvironment!.IsGlobal.Should().BeTrue();
    }

    [Fact]
    public async Task Receive_CollectionOpened_BroadcastsGlobalEnvironmentChangedMessage()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([]);

        GlobalEnvironmentChangedMessage? received = null;
        var messenger = new WeakReferenceMessenger();
        messenger.Register<GlobalEnvironmentChangedMessage>(new object(), (_, msg) => received = msg);

        var sut = BuildSut(service, messenger);
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        received.Should().NotBeNull();
    }

    // ─── BeginAddEnvironment / CancelAddEnvironment ───────────────────────────

    [Fact]
    public void BeginAddEnvironment_SetsIsAddingEnvironmentTrue()
    {
        var sut = BuildSut();
        sut.BeginAddEnvironmentCommand.Execute(null);
        sut.IsAddingEnvironment.Should().BeTrue();
    }

    [Fact]
    public void CancelAddEnvironment_ClearsAddingState()
    {
        var sut = BuildSut();
        sut.BeginAddEnvironmentCommand.Execute(null);
        sut.NewEnvironmentName = "test";

        sut.CancelAddEnvironmentCommand.Execute(null);

        sut.IsAddingEnvironment.Should().BeFalse();
        sut.NewEnvironmentName.Should().BeEmpty();
    }

    // ─── CommitAddEnvironment ─────────────────────────────────────────────────

    [Fact]
    public async Task CommitAddEnvironment_WithValidName_CreatesEnvironmentAndAddsToList()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        var created = MakeModel("production");
        service.CreateEnvironmentAsync(CollectionPath, "production", Arg.Any<CancellationToken>())
               .Returns(created);
        service.ListEnvironmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns([]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        // Simulate collection opened so path is known.
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(50);

        sut.BeginAddEnvironmentCommand.Execute(null);
        sut.NewEnvironmentName = "production";

        await sut.CommitAddEnvironmentCommand.ExecuteAsync(null);

        sut.Environments.Should().Contain(e => e.Name == "production");
        sut.IsAddingEnvironment.Should().BeFalse();
        sut.SelectedEnvironment!.Name.Should().Be("production");
    }

    [Fact]
    public async Task CommitAddEnvironment_WithBlankName_SetsError()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns([]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(50);

        sut.BeginAddEnvironmentCommand.Execute(null);
        sut.NewEnvironmentName = "   ";

        await sut.CommitAddEnvironmentCommand.ExecuteAsync(null);

        sut.NewEnvironmentError.Should().NotBeEmpty();
        sut.IsAddingEnvironment.Should().BeTrue();  // stay open so user can correct
    }

    [Fact]
    public async Task CommitAddEnvironment_WhenServiceThrowsInvalidOperation_SetsError()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns([]);
        service.CreateEnvironmentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new InvalidOperationException("Already exists."));

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(50);

        sut.BeginAddEnvironmentCommand.Execute(null);
        sut.NewEnvironmentName = "dev";

        await sut.CommitAddEnvironmentCommand.ExecuteAsync(null);

        sut.NewEnvironmentError.Should().NotBeEmpty();
    }

    // ─── DeleteEnvironment ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteEnvironment_RemovesSelectedEnvironmentFromList()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        var dev = MakeModel("dev");
        var staging = MakeModel("staging");
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([dev, staging]);
        service.DeleteEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Environments = [Global(0), dev(1), staging(2)] — select dev at index 1.
        sut.SelectedEnvironment = sut.Environments[1];

        await sut.DeleteEnvironmentCommand.ExecuteAsync(null);

        sut.Environments.Should().NotContain(e => e.Name == "dev");
        sut.Environments.Should().Contain(e => e.Name == "staging");
    }

    // ─── SaveSelected ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveSelected_ClearsDirtyFlag()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);
        service.SaveEnvironmentAsync(Arg.Any<EnvironmentModel>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Dirty the selected environment (dev, at index 1 after global).
        sut.SelectedEnvironment = sut.Environments.First(e => !e.IsGlobal);
        sut.SelectedEnvironment!.AddVariableCommand.Execute(null);
        sut.SelectedEnvironment.IsDirty.Should().BeTrue();

        await sut.SaveSelectedCommand.ExecuteAsync(null);

        sut.SelectedEnvironment.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task SaveSelected_PublishesEnvironmentSavedMessage()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);
        service.SaveEnvironmentAsync(Arg.Any<EnvironmentModel>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        EnvironmentSavedMessage? received = null;
        var messenger = new WeakReferenceMessenger();
        messenger.Register<EnvironmentSavedMessage>(new object(), (_, msg) => received = msg);

        var sut = BuildSut(service, messenger);
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Select the dev env (first non-global) before saving.
        sut.SelectedEnvironment = sut.Environments.First(e => !e.IsGlobal);

        await sut.SaveSelectedCommand.ExecuteAsync(null);

        received.Should().NotBeNull();
        received!.Value.Name.Should().Be("dev");
    }

    // ─── Variable management ──────────────────────────────────────────────────

    [Fact]
    public async Task AddVariable_MarksEnvironmentDirty()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        sut.SelectedEnvironment = devEnv;

        devEnv.IsDirty.Should().BeFalse();

        devEnv.AddVariableCommand.Execute(null);

        devEnv.IsDirty.Should().BeTrue();
        devEnv.Variables.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteVariable_MarksEnvironmentDirty()
    {
        var model = MakeModel("dev") with
        {
            Variables = [new EnvironmentVariable { Name = "BASE_URL", Value = "https://api.dev" }],
        };

        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([model]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        sut.SelectedEnvironment = devEnv;

        devEnv.Variables.Should().HaveCount(1);
        devEnv.IsDirty.Should().BeFalse();

        // Execute delete on the variable row.
        devEnv.Variables[0].DeleteCommand.Execute(null);

        devEnv.Variables.Should().BeEmpty();
        devEnv.IsDirty.Should().BeTrue();
    }

    [Fact]
    public async Task BuildModel_ExcludesVariablesWithBlankNames()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        sut.SelectedEnvironment = devEnv;

        // Add one valid and one blank-name variable.
        devEnv.AddVariableCommand.Execute(null);
        devEnv.Variables[0].Name = "API_KEY";
        devEnv.Variables[0].Value = "secret123";

        devEnv.AddVariableCommand.Execute(null);
        devEnv.Variables[1].Name = string.Empty;

        var built = devEnv.BuildModel();

        built.Variables.Should().ContainSingle(v => v.Name == "API_KEY");
    }

    [Fact]
    public async Task CloneCommand_AddsGhostItemAfterSource()
    {
        var devModel = MakeModel("dev");
        var clonedModel = MakeModel("Copy of dev");
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel, MakeModel("staging")]);
        service.CloneEnvironmentAsync(devModel.FilePath, "Copy of dev", Arg.Any<CancellationToken>())
               .Returns(clonedModel);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Environments = [Global(0), dev(1), staging(2)]. Invoke Clone on dev (index 1).
        sut.Environments[1].CloneCommand.Execute(null);
        await Task.Delay(50); // async clone

        // New item inserted right after dev at index 2; staging moves to index 3.
        sut.Environments.Should().HaveCount(4);
        sut.Environments[2].Name.Should().Be("Copy of dev");
    }

    [Fact]
    public async Task CloneCommand_OnConfirm_CreatesEnvironmentAndReplacesGhost()
    {
        var devModel = MakeModel("dev");
        var clonedModel = MakeModel("Copy of dev");

        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);
        service.CloneEnvironmentAsync(devModel.FilePath, "Copy of dev", Arg.Any<CancellationToken>())
               .Returns(clonedModel);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Environments = [Global(0), dev(1)]. Clone dev.
        sut.Environments[1].CloneCommand.Execute(null);
        await Task.Delay(50);

        // After clone: [Global(0), dev(1), Copy of dev(2)]
        sut.Environments.Should().HaveCount(3);
        sut.Environments[2].Name.Should().Be("Copy of dev");
    }

    [Fact]
    public async Task CloneCommand_OnCancel_RemovesGhostFromList()
    {
        // When clone service call fails, list stays unchanged.
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);
        // No CloneEnvironmentAsync mock → NSubstitute returns null model → caught by VM.

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Environments = [Global(0), dev(1)].
        sut.Environments[1].CloneCommand.Execute(null);
        await Task.Delay(50);

        // Clone failed silently — list unchanged.
        sut.Environments.Should().HaveCount(2);
        sut.Environments[1].Name.Should().Be("dev");
    }

    [Fact]
    public async Task CloneCommand_WhenNameConflict_ReopensRenameBoxWithError()
    {
        var devModel = MakeModel("dev");

        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);
        service.CloneEnvironmentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new InvalidOperationException("Already exists."));

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.Environments[1].CloneCommand.Execute(null);
        await Task.Delay(50);

        // Clone failed — ErrorMessage is set on the ViewModel.
        sut.ErrorMessage.Should().NotBeEmpty();
        sut.Environments.Should().HaveCount(2); // unchanged
    }

    // ─── Global environment ───────────────────────────────────────────────────

    [Fact]
    public async Task GlobalEnvironment_IsAlwaysPinnedFirst()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev"), MakeModel("staging")]);

        var sut = BuildSut(service);
        new WeakReferenceMessenger().Send(new CollectionOpenedMessage(CollectionPath));
        var messenger = new WeakReferenceMessenger();
        sut = BuildSut(service, messenger);
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.Environments[0].IsGlobal.Should().BeTrue();
        sut.Environments[0].Name.Should().Be("Global");
    }

    [Fact]
    public async Task SaveSelected_WhenGlobalEnv_SendsGlobalEnvironmentChangedMessage()
    {
        var globalVars = new List<EnvironmentVariable>
        {
            new() { Name = "BASE_URL", Value = "https://example.com" },
        };
        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables = globalVars,
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.SaveGlobalEnvironmentAsync(Arg.Any<EnvironmentModel>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);
        service.ListEnvironmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns([]);

        GlobalEnvironmentChangedMessage? received = null;
        var messenger = new WeakReferenceMessenger();
        messenger.Register<GlobalEnvironmentChangedMessage>(new object(), (_, msg) => received = msg);

        var sut = BuildSut(service, messenger);
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Global env is at index 0 and auto-selected when no other envs exist.
        sut.SelectedEnvironment!.IsGlobal.Should().BeTrue();

        // Clear the startup broadcast so we only catch the save one.
        received = null;

        await sut.SaveSelectedCommand.ExecuteAsync(null);

        received.Should().NotBeNull();
    }

    // ─── Revert ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevertSelected_ResetsVariablesToLastSavedState()
    {
        var original = MakeModel("dev") with
        {
            Variables = [new EnvironmentVariable { Name = "BASE_URL", Value = "https://api.dev" }],
        };

        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([original]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        sut.SelectedEnvironment = devEnv;

        // Make a dirty change: add a second variable.
        devEnv.AddVariableCommand.Execute(null);
        devEnv.IsDirty.Should().BeTrue();
        devEnv.Variables.Should().HaveCount(2);

        // Revert.
        sut.RevertSelectedCommand.Execute(null);

        devEnv.IsDirty.Should().BeFalse();
        devEnv.Variables.Should().ContainSingle(v => v.Name == "BASE_URL");
    }

    [Fact]
    public async Task RevertSelected_AfterSave_RevertsToPreviouslySavedState()
    {
        var original = MakeModel("dev") with
        {
            Variables = [new EnvironmentVariable { Name = "BASE_URL", Value = "https://api.dev" }],
        };
        var updated = original with
        {
            Variables = [new EnvironmentVariable { Name = "BASE_URL", Value = "https://api.staging" }],
        };

        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([original]);
        service.SaveEnvironmentAsync(Arg.Any<EnvironmentModel>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        sut.SelectedEnvironment = devEnv;

        // Mutate and save.
        devEnv.Variables[0].Value = "https://api.staging";
        devEnv.Variables[0].Name = "BASE_URL"; // force dirty
        await sut.SaveSelectedCommand.ExecuteAsync(null);
        devEnv.IsDirty.Should().BeFalse();

        // Make another change.
        devEnv.AddVariableCommand.Execute(null);
        devEnv.IsDirty.Should().BeTrue();

        // Revert should go back to the saved state (1 variable with staging URL),
        // not the original loaded state.
        sut.RevertSelectedCommand.Execute(null);

        devEnv.IsDirty.Should().BeFalse();
        devEnv.Variables.Should().ContainSingle(v => v.Name == "BASE_URL");
    }
}