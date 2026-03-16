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

    private static EnvironmentEditorViewModel BuildSut(
        IEnvironmentService? service = null,
        IMessenger? messenger = null)
    {
        service ??= Substitute.For<IEnvironmentService>();
        messenger ??= new WeakReferenceMessenger();
        return new EnvironmentEditorViewModel(
            service,
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

    // ─── CollectionOpenedMessage ──────────────────────────────────────────────

    [Fact]
    public async Task Receive_CollectionOpened_LoadsEnvironments()
    {
        var service = Substitute.For<IEnvironmentService>();
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev"), MakeModel("staging")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));

        // Give async handler a chance to complete (it's fire-and-forget).
        await Task.Delay(100);

        sut.Environments.Should().HaveCount(2);
        sut.Environments[0].Name.Should().Be("dev");
        sut.Environments[1].Name.Should().Be("staging");
    }

    [Fact]
    public async Task Receive_CollectionOpened_SelectsFirstEnvironment()
    {
        var service = Substitute.For<IEnvironmentService>();
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev"), MakeModel("staging")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.SelectedEnvironment.Should().NotBeNull();
        sut.SelectedEnvironment!.Name.Should().Be("dev");
    }

    [Fact]
    public async Task Receive_CollectionOpened_WhenNoEnvironments_SelectedEnvironmentIsNull()
    {
        var service = Substitute.For<IEnvironmentService>();
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns(Array.Empty<EnvironmentModel>());

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.SelectedEnvironment.Should().BeNull();
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

        sut.Environments.Should().ContainSingle(e => e.Name == "production");
        sut.IsAddingEnvironment.Should().BeFalse();
        sut.SelectedEnvironment!.Name.Should().Be("production");
    }

    [Fact]
    public async Task CommitAddEnvironment_WithBlankName_SetsError()
    {
        var service = Substitute.For<IEnvironmentService>();
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

        sut.SelectedEnvironment = sut.Environments[0];   // select "dev"

        await sut.DeleteEnvironmentCommand.ExecuteAsync(null);

        sut.Environments.Should().ContainSingle(e => e.Name == "staging");
    }

    // ─── SaveSelected ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveSelected_ClearsDirtyFlag()
    {
        var service = Substitute.For<IEnvironmentService>();
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);
        service.SaveEnvironmentAsync(Arg.Any<EnvironmentModel>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Dirty the selected environment.
        sut.SelectedEnvironment!.AddVariableCommand.Execute(null);
        sut.SelectedEnvironment.IsDirty.Should().BeTrue();

        await sut.SaveSelectedCommand.ExecuteAsync(null);

        sut.SelectedEnvironment.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task SaveSelected_PublishesEnvironmentSavedMessage()
    {
        var service = Substitute.For<IEnvironmentService>();
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

        await sut.SaveSelectedCommand.ExecuteAsync(null);

        received.Should().NotBeNull();
        received!.Value.Name.Should().Be("dev");
    }

    // ─── Variable management ──────────────────────────────────────────────────

    [Fact]
    public async Task AddVariable_MarksEnvironmentDirty()
    {
        var service = Substitute.For<IEnvironmentService>();
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.SelectedEnvironment!.IsDirty.Should().BeFalse();

        sut.SelectedEnvironment.AddVariableCommand.Execute(null);

        sut.SelectedEnvironment.IsDirty.Should().BeTrue();
        sut.SelectedEnvironment.Variables.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteVariable_MarksEnvironmentDirty()
    {
        var model = MakeModel("dev") with
        {
            Variables = [new EnvironmentVariable { Name = "BASE_URL", Value = "https://api.dev" }],
        };

        var service = Substitute.For<IEnvironmentService>();
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([model]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        sut.SelectedEnvironment!.Variables.Should().HaveCount(1);
        sut.SelectedEnvironment.IsDirty.Should().BeFalse();

        // Execute delete on the variable row.
        sut.SelectedEnvironment.Variables[0].DeleteCommand.Execute(null);

        sut.SelectedEnvironment.Variables.Should().BeEmpty();
        sut.SelectedEnvironment.IsDirty.Should().BeTrue();
    }

    [Fact]
    public async Task BuildModel_ExcludesVariablesWithBlankNames()
    {
        var service = Substitute.For<IEnvironmentService>();
        service.ListEnvironmentsAsync(CollectionPath, Arg.Any<CancellationToken>())
               .Returns([MakeModel("dev")]);

        var messenger = new WeakReferenceMessenger();
        var sut = BuildSut(service, messenger);

        messenger.Send(new CollectionOpenedMessage(CollectionPath));
        await Task.Delay(100);

        // Add one valid and one blank-name variable.
        sut.SelectedEnvironment!.AddVariableCommand.Execute(null);
        sut.SelectedEnvironment.Variables[0].Name = "API_KEY";
        sut.SelectedEnvironment.Variables[0].Value = "secret123";

        sut.SelectedEnvironment.AddVariableCommand.Execute(null);
        sut.SelectedEnvironment.Variables[1].Name = string.Empty;

        var built = sut.SelectedEnvironment.BuildModel();

        built.Variables.Should().ContainSingle(v => v.Name == "API_KEY");
    }
}
