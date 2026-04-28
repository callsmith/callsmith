using Callsmith.Core.Abstractions;
using Callsmith.Core.Services;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Core.Tests.Services;

public sealed class UndoRedoServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IUndoableAction MakeAction(string description = "test", string contextType = "request")
    {
        var action = Substitute.For<IUndoableAction>();
        action.Description.Returns(description);
        action.ContextType.Returns(contextType);
        return action;
    }

    private static UndoRedoService CreateSut() => new();

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void InitialState_BothStacksEmpty()
    {
        var sut = CreateSut();

        sut.CanUndo.Should().BeFalse();
        sut.CanRedo.Should().BeFalse();
        sut.UndoDescription.Should().BeNull();
        sut.RedoDescription.Should().BeNull();
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Push_SingleAction_CanUndoBecomesTrue()
    {
        var sut = CreateSut();

        sut.Push(MakeAction("Edit A"));

        sut.CanUndo.Should().BeTrue();
        sut.CanRedo.Should().BeFalse();
        sut.UndoDescription.Should().Be("Edit A");
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        var sut = CreateSut();
        sut.Push(MakeAction("A"));
        sut.Undo();
        sut.CanRedo.Should().BeTrue();

        sut.Push(MakeAction("B"));

        sut.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Push_BeyondMaxDepth_DropsOldestEntry()
    {
        var sut = CreateSut();

        // Push 201 entries (max is 200).
        for (var i = 0; i < 201; i++)
            sut.Push(MakeAction($"Action {i}"));

        sut.CanUndo.Should().BeTrue();

        // Undo all 200 retained entries.
        var undoneCount = 0;
        while (sut.CanUndo)
        {
            sut.Undo();
            undoneCount++;
        }

        undoneCount.Should().Be(200);
    }

    [Fact]
    public void Push_RaisesStackChanged()
    {
        var sut = CreateSut();
        var raised = 0;
        sut.StackChanged += (_, _) => raised++;

        sut.Push(MakeAction());

        raised.Should().Be(1);
    }

    [Fact]
    public void Push_NullAction_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.Push(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Undo ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Undo_MovesActionToRedoStack()
    {
        var sut = CreateSut();
        var action = MakeAction("Edit");
        sut.Push(action);

        var undone = sut.Undo();

        undone.Should().BeSameAs(action);
        sut.CanUndo.Should().BeFalse();
        sut.CanRedo.Should().BeTrue();
        sut.RedoDescription.Should().Be("Edit");
    }

    [Fact]
    public void Undo_WhenStackIsEmpty_ReturnsNull()
    {
        var sut = CreateSut();

        var result = sut.Undo();

        result.Should().BeNull();
    }

    [Fact]
    public void Undo_MultipleActions_UnwindsInLifoOrder()
    {
        var sut = CreateSut();
        var a = MakeAction("A");
        var b = MakeAction("B");
        sut.Push(a);
        sut.Push(b);

        sut.Undo().Should().BeSameAs(b);
        sut.Undo().Should().BeSameAs(a);
        sut.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Undo_RaisesStackChanged()
    {
        var sut = CreateSut();
        sut.Push(MakeAction());
        var raised = 0;
        sut.StackChanged += (_, _) => raised++;

        sut.Undo();

        raised.Should().Be(1);
    }

    // ── Redo ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Redo_MovesActionBackToUndoStack()
    {
        var sut = CreateSut();
        var action = MakeAction("Edit");
        sut.Push(action);
        sut.Undo();

        var redone = sut.Redo();

        redone.Should().BeSameAs(action);
        sut.CanUndo.Should().BeTrue();
        sut.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Redo_WhenStackIsEmpty_ReturnsNull()
    {
        var sut = CreateSut();

        var result = sut.Redo();

        result.Should().BeNull();
    }

    [Fact]
    public void Redo_MultipleActions_ReappliesInLifoOrder()
    {
        var sut = CreateSut();
        var a = MakeAction("A");
        var b = MakeAction("B");
        sut.Push(a);
        sut.Push(b);
        sut.Undo(); // undo b
        sut.Undo(); // undo a

        sut.Redo().Should().BeSameAs(a);
        sut.Redo().Should().BeSameAs(b);
        sut.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Redo_RaisesStackChanged()
    {
        var sut = CreateSut();
        sut.Push(MakeAction());
        sut.Undo();
        var raised = 0;
        sut.StackChanged += (_, _) => raised++;

        sut.Redo();

        raised.Should().Be(1);
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_EmptiesBothStacks()
    {
        var sut = CreateSut();
        sut.Push(MakeAction("A"));
        sut.Push(MakeAction("B"));
        sut.Undo();

        sut.Clear();

        sut.CanUndo.Should().BeFalse();
        sut.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Clear_RaisesStackChanged()
    {
        var sut = CreateSut();
        sut.Push(MakeAction());
        var raised = 0;
        sut.StackChanged += (_, _) => raised++;

        sut.Clear();

        raised.Should().Be(1);
    }

    // ── Combined scenarios ────────────────────────────────────────────────────

    [Fact]
    public void UndoThenNewEdit_ClearsRedoAndStartsFreshBranch()
    {
        var sut = CreateSut();
        var a = MakeAction("A");
        var b = MakeAction("B");
        var c = MakeAction("C");
        sut.Push(a);
        sut.Push(b);
        sut.Undo(); // undo b → redo has b

        sut.Push(c); // new edit — clears redo

        sut.CanRedo.Should().BeFalse();
        sut.UndoDescription.Should().Be("C");
        sut.Undo().Should().BeSameAs(c);
        sut.Undo().Should().BeSameAs(a);
    }
}
