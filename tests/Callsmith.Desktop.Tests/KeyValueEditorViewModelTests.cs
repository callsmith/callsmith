using Callsmith.Desktop.ViewModels;
using Callsmith.Core.Models;
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

    [Fact]
    public void GetEnabledPairs_WhenFileRowsExist_ReturnsOnlyTextRows()
    {
        var sut = new KeyValueEditorViewModel { ShowValueTypeSelector = true };
        var text = new KeyValueItemViewModel(_ => { }) { Key = "name", Value = "alice" };
        var file = new KeyValueItemViewModel(_ => { }) { Key = "avatar", ValueType = KeyValueItemViewModel.ValueTypes.File };
        file.LoadFile([0x01], "a.bin", "/tmp/a.bin");
        sut.Items.Add(text);
        sut.Items.Add(file);

        var pairs = sut.GetEnabledPairs().ToList();

        pairs.Should().ContainSingle(p => p.Key == "name" && p.Value == "alice");
    }

    [Fact]
    public void GetEnabledMultipartFileParts_ReturnsOnlyEnabledFileRows()
    {
        var sut = new KeyValueEditorViewModel { ShowValueTypeSelector = true };
        var file1 = new KeyValueItemViewModel(_ => { }) { Key = "file1", ValueType = KeyValueItemViewModel.ValueTypes.File, IsEnabled = true };
        file1.LoadFile([0x10], "f1.bin", "/tmp/f1.bin");
        var file2 = new KeyValueItemViewModel(_ => { }) { Key = "file2", ValueType = KeyValueItemViewModel.ValueTypes.File, IsEnabled = false };
        file2.LoadFile([0x20], "f2.bin", "/tmp/f2.bin");
        sut.Items.Add(file1);
        sut.Items.Add(file2);

        var files = sut.GetEnabledMultipartFileParts();

        files.Should().ContainSingle();
        files[0].Key.Should().Be("file1");
        files[0].FileName.Should().Be("f1.bin");
    }
}
