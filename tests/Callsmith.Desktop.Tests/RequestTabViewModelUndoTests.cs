using System.Net.Http;
using Avalonia.Headless.XUnit;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Actions;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

/// <summary>
/// Verifies that <see cref="RequestTabViewModel"/> correctly integrates with
/// <see cref="IUndoRedoService"/>: immediate per-change memento push and ApplySnapshot.
/// </summary>
public sealed class RequestTabViewModelUndoTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (RequestTabViewModel Tab, IUndoRedoService UndoService) BuildSut()
    {
        var undoService = new UndoRedoService();
        var cs = Substitute.For<ICollectionService>();
        cs.SaveRequestAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var tab = new RequestTabViewModel(
            new TransportRegistry(),
            cs,
            new WeakReferenceMessenger(),
            _ => { },
            undoRedoService: undoService);

        return (tab, undoService);
    }

    private static CollectionRequest MakeRequest(string url = "https://api.example.com/") =>
        new()
        {
            FilePath = @"C:\collections\req\sample.callsmith",
            Name = "sample",
            Method = HttpMethod.Get,
            Url = url,
        };

    // ── Initial baseline ──────────────────────────────────────────────────────

    [AvaloniaFact]
    public void AfterLoadRequest_UndoServiceIsEmpty()
    {
        var (tab, undo) = BuildSut();
        tab.LoadRequest(MakeRequest());

        undo.CanUndo.Should().BeFalse();
        undo.CanRedo.Should().BeFalse();
    }

    // ── Immediate push on edit ────────────────────────────────────────────────

    [AvaloniaFact]
    public void Edit_PushesActionImmediately()
    {
        var (tab, undo) = BuildSut();
        tab.LoadRequest(MakeRequest("https://api.example.com/original"));

        tab.Url = "https://api.example.com/modified";

        undo.CanUndo.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Edit_WhenValueUnchangedFromBaseline_DoesNotPush()
    {
        var (tab, undo) = BuildSut();
        tab.LoadRequest(MakeRequest("https://api.example.com/"));

        // Set to the same URL — equality check should suppress the push.
        tab.Url = "https://api.example.com/";

        undo.CanUndo.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Edit_ActionHasCorrectBeforeAndAfter()
    {
        var (tab, undo) = BuildSut();
        tab.LoadRequest(MakeRequest("https://original.example.com/"));

        tab.Url = "https://modified.example.com/";

        var action = (RequestTabMementoAction)undo.Undo()!;
        action.Before.Url.Should().Be("https://original.example.com/");
        action.After.Url.Should().Be("https://modified.example.com/");
    }

    [AvaloniaFact]
    public void Edit_AdvancesBaseline_SameValueAgainDoesNotPushDuplicate()
    {
        var (tab, undo) = BuildSut();
        tab.LoadRequest(MakeRequest("https://a.example.com/"));

        tab.Url = "https://b.example.com/"; // pushes A→B, baseline becomes B
        tab.Url = "https://b.example.com/"; // identical to new baseline — no push

        undo.CanUndo.Should().BeTrue();
        undo.Undo();
        undo.CanUndo.Should().BeFalse();
    }

    [AvaloniaFact]
    public void MultipleEdits_EachDistinctChangeCreatesEntry()
    {
        var (tab, undo) = BuildSut();
        tab.LoadRequest(MakeRequest("https://a.example.com/"));

        tab.Url = "https://b.example.com/";
        tab.Url = "https://c.example.com/";

        // Two entries on undo stack.
        undo.Undo()!.Description.Should().Be("Edit request");
        undo.Undo()!.Description.Should().NotBeNull();
        undo.CanUndo.Should().BeFalse();
    }

    // ── ApplySnapshot ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ApplySnapshot_RestoresEditorUrl()
    {
        var (tab, _) = BuildSut();
        tab.LoadRequest(MakeRequest("https://original.example.com/"));

        tab.Url = "https://modified.example.com/";
        var snapshot = MakeRequest("https://original.example.com/");
        tab.ApplySnapshot(snapshot);

        tab.Url.Should().Be("https://original.example.com/");
    }

    [AvaloniaFact]
    public void ApplySnapshot_DoesNotPushToUndoStack()
    {
        var (tab, undo) = BuildSut();
        tab.LoadRequest(MakeRequest("https://original.example.com/"));

        var snapshot = MakeRequest("https://original.example.com/");
        tab.ApplySnapshot(snapshot);

        undo.CanUndo.Should().BeFalse();
    }

    [AvaloniaFact]
    public void ApplySnapshot_UpdatesHasUnsavedChanges_WhenSnapshotMatchesSavedState()
    {
        var (tab, _) = BuildSut();
        var req = MakeRequest("https://api.example.com/");
        tab.LoadRequest(req);

        tab.Url = "https://modified.example.com/";
        tab.HasUnsavedChanges.Should().BeTrue();

        // Apply the original snapshot.
        tab.ApplySnapshot(req);

        tab.HasUnsavedChanges.Should().BeFalse();
    }
}
