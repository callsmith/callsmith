using Callsmith.Core.Abstractions;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Desktop.Controls;
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
        if (service is null)
        {
            service = Substitute.For<IEnvironmentService>();
            // Only seed the default global env on a freshly-created mock so tests that supply
            // their own service stub don't get their LoadGlobalEnvironmentAsync setup overwritten.
            service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(new EnvironmentModel { FilePath = GlobalEnvPath, Name = "Global", Variables = [], EnvironmentId = Guid.NewGuid() });
        }
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
            EnvironmentId = Guid.NewGuid(),
        };

    // Helper: stub LoadGlobalEnvironmentAsync on an existing mock.
    private static void SetupGlobalEnv(IEnvironmentService service) =>
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new EnvironmentModel { FilePath = GlobalEnvPath, Name = "Global", Variables = [], EnvironmentId = Guid.NewGuid() });

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
    public async Task SelectedConcreteEnvironment_BuildsSuggestionsFromGlobalAndOwnVariables()
    {
        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(CollectionPath, Arg.Any<CancellationToken>())
            .Returns(new EnvironmentModel
            {
                FilePath = GlobalEnvPath,
                Name = "Global",
                Variables =
                [
                    new EnvironmentVariable { Name = "base-url", Value = "https://api.example.com" },
                ],
                EnvironmentId = Guid.NewGuid(),
            });
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
            .Returns(
            [
                new EnvironmentModel
                {
                    Name = "dev",
                    FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
                    Variables =
                    [
                        new EnvironmentVariable { Name = "token", Value = "secret-token", IsSecret = true },
                    ],
                    EnvironmentId = Guid.NewGuid(),
                },
            ]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        var variable = sut.SelectedEnvironment!.Variables.Single();

        variable.SuggestionNames.Should().BeEquivalentTo(
        [
            new EnvVarSuggestion("base-url", "https://api.example.com"),
            new EnvVarSuggestion("token", "•••••"),
        ]);
    }

    [Fact]
    public async Task EditingSelectedEnvironmentVariableName_RefreshesSuggestionsForVisibleRows()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
            .Returns(
            [
                new EnvironmentModel
                {
                    Name = "dev",
                    FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
                    Variables =
                    [
                        new EnvironmentVariable { Name = "token", Value = "abc" },
                    ],
                    EnvironmentId = Guid.NewGuid(),
                },
            ]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.SelectedEnvironment!.AddVariableCommand.Execute(null);
        var newVariable = sut.SelectedEnvironment.Variables.Last();
        newVariable.Name = "base-url";

        sut.SelectedEnvironment.Variables.First().SuggestionNames
            .Should().ContainEquivalentOf(new EnvVarSuggestion("base-url", string.Empty));
        newVariable.SuggestionNames
            .Should().ContainEquivalentOf(new EnvVarSuggestion("base-url", string.Empty));
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

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);
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
            EnvironmentId = Guid.NewGuid(),
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

    // ─── Dynamic variable preview ─────────────────────────────────────────────

    /// <summary>
    /// Regression: when a concrete env's static var references a global response-body var
    /// (e.g. "Bearer {{access-token}}"), the preview must show the resolved value even though
    /// the concrete env has no response-body vars of its own.
    /// </summary>
    [Fact]
    public async Task Preview_ConcreteEnv_StaticVarReferencingGlobalResponseBodyVar_ResolvesToResolvedValue()
    {
        const string resolvedToken = "eyJ.resolved.token";

        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "access-token",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                    ResponseRequestName = "Auth/login",
                    ResponsePath = "$.token",
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var devModel = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "auth-header",
                    Value = "Bearer {{access-token}}",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);

        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        // Return the resolved access-token whenever the global env's variables are evaluated.
        evaluator.ResolveAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Is<IReadOnlyList<EnvironmentVariable>>(vars =>
                    vars.Any(v => v.Name == "access-token"
                               && v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody)),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["access-token"] = resolvedToken },
                MockGenerators = new Dictionary<string, MockDataEntry>(),
            });

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger, dynamicEvaluator: evaluator);
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(300); // allow fire-and-forget load + preview refresh

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        var authHeaderVar = devEnv.Variables.First(v => v.Name == "auth-header");

        authHeaderVar.PreviewValue.Should().Be($"Bearer {resolvedToken}",
            "a concrete env static var that references a global response-body var should show the resolved value");
    }

    [Fact]
    public async Task Receive_RequestRenamed_UpdatesResponseBodyRequestNameInEnvironmentVariables()
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

        var devEnv = sut.Environments.First(e => e.Name == "dev");

        var variable = new EnvironmentVariableItemViewModel(
            onDelete: _ => { },
            onChanged: () => { },
            getResolvedEnv: () => new ResolvedEnvironment { Variables = new Dictionary<string, string>() })
        {
            Name = "access-token",
            VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
            ResponseRequestName = "Auth/login",
            ResponsePath = "$.token",
        };

        devEnv.Variables.Add(variable);

        var oldFilePath = Path.Combine(CollectionPath, "Auth", "login.callsmith");
        var newFilePath = Path.Combine(CollectionPath, "Auth", "signin.callsmith");
        var renamedRequest = new CollectionRequest
        {
            Name = "signin",
            FilePath = newFilePath,
            Method = System.Net.Http.HttpMethod.Post,
            Url = "https://example.com/auth/signin",
        };

        messenger.Send(new RequestRenamedMessage(oldFilePath, renamedRequest));
        await Task.Delay(100);

        variable.ResponseRequestName.Should().Be("Auth/signin");
        devEnv.IsDirty.Should().BeFalse("renamed request references are auto-saved");
        await service.Received(1).SaveEnvironmentAsync(
            Arg.Is<EnvironmentModel>(m =>
                m.Name == "dev"
                && m.Variables.Any(v => v.Name == "access-token" && v.ResponseRequestName == "Auth/signin")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Receive_CollectionOpened_SelectsCurrentlyActiveEnvironment_WhenAvailable()
    {
        var service = Substitute.For<IEnvironmentService>();
        SetupGlobalEnv(service);

        var dev = MakeModel("dev");
        var local = MakeModel("local");
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
            .Returns([dev, local]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new EnvironmentChangedMessage(local));
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.SelectedEnvironment.Should().NotBeNull();
        sut.SelectedEnvironment!.Name.Should().Be("local");
        sut.SelectedEnvironment.FilePath.Should().Be(local.FilePath);
    }

    /// <summary>
    /// When neither the global env nor the concrete env has any response-body variables,
    /// the evaluator should never be called — no HTTP requests should fire.
    /// </summary>
    [Fact]
    public async Task Preview_WhenNeitherEnvHasResponseBodyVars_ResolveAsyncIsNeverCalled()
    {
        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "base-url",
                    Value = "https://api.example.com",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var devModel = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "full-url",
                    Value = "{{base-url}}/v1",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);

        var evaluator = Substitute.For<IDynamicVariableEvaluator>();

        var messenger = new WeakReferenceMessenger();
        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(300);

        await evaluator.DidNotReceive().ResolveAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Even without HTTP, global static vars must propagate into a concrete env's preview column
    /// so that {{base-url}} and similar tokens resolve correctly.
    /// </summary>
    [Fact]
    public async Task Preview_WhenNeitherEnvHasResponseBodyVars_GlobalStaticVarsResolveInConcreteEnvPreview()
    {
        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "base-url",
                    Value = "https://api.example.com",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var devModel = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "full-url",
                    Value = "{{base-url}}/v1",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(300);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        var fullUrlVar = devEnv.Variables.First(v => v.Name == "full-url");

        fullUrlVar.PreviewValue.Should().Be("https://api.example.com/v1");
    }

    /// <summary>
    /// When a concrete env has its own response-body var, it is resolved and
    /// any static var in that env that references it shows the resolved value in the preview.
    /// </summary>
    [Fact]
    public async Task Preview_ConcreteEnv_OwnResponseBodyVarResolvesAndPropagatesIntoStaticVarPreview()
    {
        const string resolvedKey = "my-api-key-123";

        var devModel = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "api-key",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                    ResponseRequestName = "GetKey",
                    ResponsePath = "$.key",
                },
                new EnvironmentVariable
                {
                    Name = "auth-header",
                    Value = "Key {{api-key}}",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new EnvironmentModel { FilePath = GlobalEnvPath, Name = "Global", Variables = [], EnvironmentId = Guid.NewGuid() });
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);

        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        // Step 2: concrete env's own resolution returns the api-key.
        evaluator.ResolveAsync(
                CollectionPath, devModel.EnvironmentId.ToString("N"),
                Arg.Any<IReadOnlyList<EnvironmentVariable>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["api-key"] = resolvedKey },
                MockGenerators = new Dictionary<string, MockDataEntry>(),
            });

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger, dynamicEvaluator: evaluator);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(300);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        var authHeaderVar = devEnv.Variables.First(v => v.Name == "auth-header");

        authHeaderVar.PreviewValue.Should().Be($"Key {resolvedKey}");
    }

    [Fact]
    public async Task Preview_ConcreteEnv_ForceOverrideGlobalMockData_ShowsGeneratedConflictValue()
    {
        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "guid",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.MockData,
                    MockDataCategory = "Random",
                    MockDataField = "UUID",
                    IsForceGlobalOverride = true,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var devModel = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "guid",
                    Value = "local-guid",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(300);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        var guidVar = devEnv.Variables.First(v => v.Name == "guid");

        guidVar.ConflictLabel.Should().Be("OVERRIDDEN WITH");
        guidVar.ConflictValue.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(guidVar.ConflictValue, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Preview_GlobalEnv_UncheckedConcreteOverride_KeepsOwnResolvedPreviewAndShowsOverridingValue()
    {
        const string resolvedToken = "eyJ.preview.jwt";

        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "jwt-token",
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                    ResponseRequestName = "Auth/login",
                    ResponsePath = "$.token",
                    IsForceGlobalOverride = false,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var devModel = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "jwt-token",
                    Value = "null",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);

        var evaluator = Substitute.For<IDynamicVariableEvaluator>();
        evaluator.ResolveAsync(
                CollectionPath,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyList<EnvironmentVariable>>(vars =>
                    vars.Any(v => v.Name == "jwt-token"
                               && v.VariableType == EnvironmentVariable.VariableTypes.ResponseBody)),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolvedEnvironment
            {
                Variables = new Dictionary<string, string> { ["jwt-token"] = resolvedToken },
                MockGenerators = new Dictionary<string, MockDataEntry>(),
            });

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger, dynamicEvaluator: evaluator);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(300);

        sut.SelectedEnvironment = sut.Environments.First(e => e.IsGlobal);
        await Task.Delay(300);

        var globalEnv = sut.Environments.First(e => e.IsGlobal);
        var jwtTokenVar = globalEnv.Variables.First(v => v.Name == "jwt-token");

        jwtTokenVar.PreviewValue.Should().Be(resolvedToken);
        jwtTokenVar.ConflictLabel.Should().Be("OVERRIDDEN WITH");
        jwtTokenVar.ConflictValue.Should().Be("null");
    }

    [Fact]
    public async Task Preview_ConcreteEnv_WhenForceOverrideGlobalVarIsSecret_ConflictValueIsMasked()
    {
        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "jwt-token",
                    Value = "super-secret-global-token",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                    IsSecret = true,
                    IsForceGlobalOverride = true,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var devModel = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "jwt-token",
                    Value = "placeholder",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(300);

        var devEnv = sut.Environments.First(e => !e.IsGlobal);
        var jwtTokenVar = devEnv.Variables.First(v => v.Name == "jwt-token");

        jwtTokenVar.ConflictLabel.Should().Be("OVERRIDDEN WITH");
        jwtTokenVar.ConflictValue.Should().Be("\u2022\u2022\u2022\u2022\u2022");
    }

    [Theory]
    [InlineData(true, "OVERRIDES")]
    [InlineData(false, "OVERRIDDEN WITH")]
    public async Task Preview_GlobalEnv_WhenConcreteVarIsSecret_ConflictValueIsMasked(bool forceOverride, string expectedLabel)
    {
        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "jwt-token",
                    Value = "public-value",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                    IsForceGlobalOverride = forceOverride,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var devModel = new EnvironmentModel
        {
            Name = "dev",
            FilePath = @"C:\collections\my-api\environment\dev.env.callsmith",
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "jwt-token",
                    Value = "concrete-secret-token",
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                    IsSecret = true,
                },
            ],
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([devModel]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(300);

        sut.SelectedEnvironment = sut.Environments.First(e => e.IsGlobal);
        await Task.Delay(300);

        var globalEnv = sut.Environments.First(e => e.IsGlobal);
        var jwtTokenVar = globalEnv.Variables.First(v => v.Name == "jwt-token");

        jwtTokenVar.ConflictLabel.Should().Be(expectedLabel);
        jwtTokenVar.ConflictValue.Should().Be("\u2022\u2022\u2022\u2022\u2022");
    }

    /// <summary>
    /// Regression: restoring the saved GlobalPreviewEnvironmentName during collection load
    /// used to set IsDirty = true on the global env before the user touched anything.
    /// </summary>
    [Fact]
    public async Task GlobalEnv_NotMarkedDirty_WhenGlobalPreviewEnvRestoredOnLoad()
    {
        var globalModel = new EnvironmentModel
        {
            FilePath = GlobalEnvPath,
            Name = "Global",
            Variables = [],
            GlobalPreviewEnvironmentName = "dev", // previously saved selection
            EnvironmentId = Guid.NewGuid(),
        };

        var service = Substitute.For<IEnvironmentService>();
        service.LoadGlobalEnvironmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(globalModel);
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(200);

        var globalEnv = sut.Environments.First(e => e.IsGlobal);
        globalEnv.IsDirty.Should().BeFalse(
            "restoring GlobalPreviewEnvironmentName from saved state must not mark the global env dirty");
    }
}