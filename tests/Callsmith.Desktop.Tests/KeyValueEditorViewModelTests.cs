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
        sut.LoadMultipartFrom(
            [new KeyValuePair<string, string>("name", "alice")],
            [
                new MultipartFilePart
                {
                    Key = "avatar",
                    FileBytes = [0x01],
                    FileName = "a.bin",
                    FilePath = "/tmp/a.bin",
                    IsEnabled = true,
                },
            ]);

        var pairs = sut.GetEnabledPairs().ToList();

        pairs.Should().ContainSingle(p => p.Key == "name" && p.Value == "alice");
    }

    [Fact]
    public void GetEnabledMultipartFileParts_ReturnsOnlyEnabledFileRows()
    {
        var sut = new KeyValueEditorViewModel { ShowValueTypeSelector = true };
        sut.LoadMultipartFrom(
            Array.Empty<KeyValuePair<string, string>>(),
            [
                new MultipartFilePart
                {
                    Key = "file1",
                    FileBytes = [0x10],
                    FileName = "f1.bin",
                    FilePath = "/tmp/f1.bin",
                    IsEnabled = true,
                },
                new MultipartFilePart
                {
                    Key = "file2",
                    FileBytes = [0x20],
                    FileName = "f2.bin",
                    FilePath = "/tmp/f2.bin",
                    IsEnabled = false,
                },
            ]);

        var files = sut.GetEnabledMultipartFileParts();

        files.Should().ContainSingle();
        files[0].Key.Should().Be("file1");
        files[0].FileName.Should().Be("f1.bin");
    }

    [Fact]
    public void LoadMultipartFrom_WithoutPersistedPath_UsesFileNameForDisplay()
    {
        var sut = new KeyValueEditorViewModel { ShowValueTypeSelector = true };
        sut.LoadMultipartFrom(
            [
                new MultipartBodyEntry
                {
                    Key = "attachment",
                    IsFile = true,
                    FileName = "file1.txt",
                    IsEnabled = true,
                },
            ],
            [
                new MultipartFilePart
                {
                    Key = "attachment",
                    FileBytes = [0x01, 0x02],
                    FileName = "file1.txt",
                    FilePath = null,
                    IsEnabled = true,
                },
            ]);

        sut.Items.Should().ContainSingle();
        var fileRow = sut.Items[0];
        fileRow.IsFileValue.Should().BeTrue();
        fileRow.HasSelectedFile.Should().BeTrue();
        fileRow.SelectedFilePath.Should().Be("file1.txt");
    }

    [Fact]
    public void AllItemsEnabled_WhenMixedRows_IsIndeterminate()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"));
        sut.Items[0].IsEnabled = true;
        sut.Items[1].IsEnabled = false;

        sut.AllItemsEnabled.Should().BeNull();
    }

    [Fact]
    public void AllItemsEnabled_WhenSetTrue_EnablesAllRows()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"));
        sut.Items[0].IsEnabled = false;
        sut.Items[1].IsEnabled = false;

        sut.AllItemsEnabled = true;

        sut.Items.Should().OnlyContain(i => i.IsEnabled);
        sut.AllItemsEnabled.Should().BeTrue();
    }

    [Fact]
    public void AllItemsEnabled_WhenSetFalse_DisablesAllRows()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"));
        sut.Items[0].IsEnabled = true;
        sut.Items[1].IsEnabled = true;

        sut.AllItemsEnabled = false;

        sut.Items.Should().OnlyContain(i => !i.IsEnabled);
        sut.AllItemsEnabled.Should().BeFalse();
    }

    [Fact]
    public void AllItemsEnabled_WhenRowsBecomeUniform_UpdatesState()
    {
        var sut = BuildSut(("a", "1"), ("b", "2"));
        sut.Items[0].IsEnabled = true;
        sut.Items[1].IsEnabled = false;
        sut.AllItemsEnabled.Should().BeNull();

        sut.Items[1].IsEnabled = true;
        sut.AllItemsEnabled.Should().BeTrue();

        sut.Items[0].IsEnabled = false;
        sut.AllItemsEnabled.Should().BeNull();

        sut.Items[1].IsEnabled = false;
        sut.AllItemsEnabled.Should().BeFalse();
    }
}
