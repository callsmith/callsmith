using Avalonia.Headless.XUnit;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Actions;
using Callsmith.Desktop.ViewModels;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Verifies that <see cref="EnvironmentListItemViewModel"/> correctly integrates with
/// <see cref="UndoRedoService"/>: immediate per-change memento push and ApplySnapshot.
/// </summary>
public sealed class EnvironmentListItemViewModelUndoTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EnvironmentModel MakeModel(string varValue = "initial") =>
        new()
        {
            Name = "dev",
            FilePath = @"C:\collections\env\dev.env.callsmith",
            EnvironmentId = Guid.NewGuid(),
            Variables =
            [
                new EnvironmentVariable
                {
                    Name = "apiKey",
                    Value = varValue,
                    VariableType = EnvironmentVariable.VariableTypes.Static,
                }
            ],
        };

    private static (EnvironmentListItemViewModel Vm, UndoRedoService UndoService) BuildSut(
        EnvironmentModel? model = null)
    {
        var undo = new UndoRedoService();
        var vm = new EnvironmentListItemViewModel(
            model ?? MakeModel(),
            onRenameCommit: (_, _, _) => Task.CompletedTask,
            onDeleteRequest: (_, _) => Task.CompletedTask,
            undoRedoService: undo);
        return (vm, undo);
    }

    // ── Initial state ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void AfterConstruction_UndoServiceIsEmpty()
    {
        var (_, undo) = BuildSut();

        undo.CanUndo.Should().BeFalse();
        undo.CanRedo.Should().BeFalse();
    }

    // ── Immediate push on edit ────────────────────────────────────────────────

    [AvaloniaFact]
    public void Edit_PushesActionImmediately()
    {
        var (vm, undo) = BuildSut();

        vm.Variables[0].Value = "modified";

        undo.CanUndo.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Edit_WhenValueUnchangedFromBaseline_DoesNotPush()
    {
        var (vm, undo) = BuildSut(MakeModel("initial"));

        vm.Variables[0].Value = "initial"; // same as baseline

        undo.CanUndo.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Edit_ActionHasCorrectBeforeAndAfter()
    {
        var model = MakeModel("original-value");
        var (vm, undo) = BuildSut(model);

        vm.Variables[0].Value = "new-value";

        var action = (EnvironmentMementoAction)undo.Undo()!;
        action.Before.Variables[0].Value.Should().Be("original-value");
        action.After.Variables[0].Value.Should().Be("new-value");
    }

    [AvaloniaFact]
    public void Edit_AdvancesBaseline_SameValueAgainDoesNotPushDuplicate()
    {
        var (vm, undo) = BuildSut();

        vm.Variables[0].Value = "v1"; // pushes initial→v1
        vm.Variables[0].Value = "v1"; // identical to new baseline — no push

        undo.CanUndo.Should().BeTrue();
        undo.Undo();
        undo.CanUndo.Should().BeFalse();
    }

    [AvaloniaFact]
    public void MultipleEdits_EachDistinctChangeCreatesEntry()
    {
        var (vm, undo) = BuildSut();

        vm.Variables[0].Value = "v1";
        vm.Variables[0].Value = "v2";

        undo.Undo();
        undo.Undo();
        undo.CanUndo.Should().BeFalse();
    }

    // ── ApplySnapshot ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ApplySnapshot_RestoresVariableValue()
    {
        var model = MakeModel("original-value");
        var (vm, _) = BuildSut(model);

        vm.Variables[0].Value = "modified";
        vm.ApplySnapshot(model);

        vm.Variables[0].Value.Should().Be("original-value");
    }

    [AvaloniaFact]
    public void ApplySnapshot_DoesNotPushToUndoStack()
    {
        var model = MakeModel();
        var (vm, undo) = BuildSut(model);

        vm.ApplySnapshot(model);

        undo.CanUndo.Should().BeFalse();
    }

    [AvaloniaFact]
    public void ApplySnapshot_RestoresIsDirty_WhenSnapshotMatchesBaseline()
    {
        var model = MakeModel("initial");
        var (vm, _) = BuildSut(model);

        vm.Variables[0].Value = "modified";
        vm.IsDirty.Should().BeTrue();

        vm.ApplySnapshot(model);

        vm.IsDirty.Should().BeFalse();
    }
}
