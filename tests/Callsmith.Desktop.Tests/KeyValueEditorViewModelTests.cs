using Callsmith.Desktop.ViewModels;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Unit tests for <see cref="KeyValueEditorViewModel"/>, focusing on the
/// <see cref="KeyValueEditorViewModel.MoveItem"/> method used for drag-to-reorder.
/// </summary>
public sealed class KeyValueEditorViewModelTests
{
    private static KeyValueEditorViewModel BuildSut(params (string key, string value)[] rows)
    {
        var vm = new KeyValueEditorViewModel();
        foreach (var (k, v) in rows)
            vm.Items.Add(new KeyValueItemViewModel(_ => { }) { Key = k, Value = v });
        return vm;
    }

    [Fact]
    public void MoveItem_MovesItemToTargetIndex()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"), ("c", "3"));
        var item = sut.Items[0]; // "a"

        sut.MoveItem(item, 2);

        sut.Items.Select(i => i.Key).Should().Equal("b", "c", "a");
    }

    [Fact]
    public void MoveItem_MovesItemBackward()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"), ("c", "3"));
        var item = sut.Items[2]; // "c"

        sut.MoveItem(item, 0);

        sut.Items.Select(i => i.Key).Should().Equal("c", "a", "b");
    }

    [Fact]
    public void MoveItem_SameIndex_DoesNothing()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"), ("c", "3"));
        var item = sut.Items[1]; // "b"

        sut.MoveItem(item, 1);

        sut.Items.Select(i => i.Key).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void MoveItem_ItemNotInList_DoesNothing()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"));
        var outsider = new KeyValueItemViewModel(_ => { }) { Key = "x" };

        sut.MoveItem(outsider, 0);

        sut.Items.Select(i => i.Key).Should().Equal("a", "b");
    }

    [Fact]
    public void MoveItem_NegativeTargetIndex_DoesNothing()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"));
        var item = sut.Items[0];

        sut.MoveItem(item, -1);

        sut.Items.Select(i => i.Key).Should().Equal("a", "b");
    }

    [Fact]
    public void MoveItem_TargetIndexBeyondEnd_DoesNothing()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"));
        var item = sut.Items[0];

        sut.MoveItem(item, 5);

        sut.Items.Select(i => i.Key).Should().Equal("a", "b");
    }

    [Fact]
    public void MoveItem_RaisesChangedEvent()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"), ("c", "3"));
        var item = sut.Items[0];
        var eventFired = false;
        sut.Changed += (_, _) => eventFired = true;

        sut.MoveItem(item, 2);

        eventFired.Should().BeTrue();
    }
}
