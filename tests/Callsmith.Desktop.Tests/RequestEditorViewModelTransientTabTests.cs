using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Messages;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class RequestEditorViewModelTransientTabTests
{
    private static RequestEditorViewModel BuildSut()
    {
        var messenger = new WeakReferenceMessenger();
        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RequestFileExtension.Returns(".callsmith");

        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var transportRegistry = Substitute.For<ITransportRegistry>();
        var dynamicEvaluator = Substitute.For<IDynamicVariableEvaluator>();

        return new RequestEditorViewModel(
            transportRegistry,
            collectionService,
            preferencesService,
            dynamicEvaluator,
            new EnvironmentMergeService(dynamicEvaluator),
            messenger,
            NullLogger<RequestEditorViewModel>.Instance);
    }

    private static CollectionRequest MakeRequest(string filePath, string name = "My Request") =>
        new()
        {
            FilePath = filePath,
            Name = name,
            Method = HttpMethod.Get,
            Url = "https://example.com",
            Headers = [],
            PathParams = new Dictionary<string, string>(),
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            Auth = new AuthConfig(),
        };

    // ─── Opening as transient ─────────────────────────────────────────────────

    [Fact]
    public void ReceiveRequestSelected_OpensTabAsTransient()
    {
        var sut = BuildSut();
        var req = MakeRequest(@"/col/a.callsmith", "Alpha");

        sut.Receive(new RequestSelectedMessage(req));

        sut.Tabs.Should().HaveCount(1);
        sut.Tabs[0].IsTransient.Should().BeTrue();
    }

    [Fact]
    public void ReceiveRequestSelected_TransientTabHasItalicTitle_ViaIsTransientProperty()
    {
        var sut = BuildSut();
        var req = MakeRequest(@"/col/a.callsmith", "Alpha");

        sut.Receive(new RequestSelectedMessage(req));

        // The AXAML applies italic FontStyle when IsTransient is true.
        sut.Tabs[0].IsTransient.Should().BeTrue();
        sut.Tabs[0].TabTitle.Should().Be("Alpha");
    }

    // ─── Only one transient tab at a time ─────────────────────────────────────

    [Fact]
    public void OpeningSecondTransientTab_ClosesFirstTransientTab()
    {
        var sut = BuildSut();
        var reqA = MakeRequest(@"/col/a.callsmith", "Alpha");
        var reqB = MakeRequest(@"/col/b.callsmith", "Beta");

        sut.Receive(new RequestSelectedMessage(reqA));
        var tabA = sut.Tabs[0];

        sut.Receive(new RequestSelectedMessage(reqB));

        sut.Tabs.Should().HaveCount(1);
        sut.Tabs.Should().NotContain(tabA);
        sut.Tabs[0].IsTransient.Should().BeTrue();
        sut.Tabs[0].TabTitle.Should().Be("Beta");
    }

    [Fact]
    public void OpeningThirdTransientTab_ClosesSecondTransientTabToo()
    {
        var sut = BuildSut();
        sut.Receive(new RequestSelectedMessage(MakeRequest(@"/col/a.callsmith", "A")));
        sut.Receive(new RequestSelectedMessage(MakeRequest(@"/col/b.callsmith", "B")));
        sut.Receive(new RequestSelectedMessage(MakeRequest(@"/col/c.callsmith", "C")));

        sut.Tabs.Should().HaveCount(1);
        sut.Tabs[0].TabTitle.Should().Be("C");
    }

    // ─── Clicking the same request does not reopen ────────────────────────────

    [Fact]
    public void ClickingSameRequest_FocusesExistingTab_WithoutOpeningANew()
    {
        var sut = BuildSut();
        var req = MakeRequest(@"/col/a.callsmith", "Alpha");

        sut.Receive(new RequestSelectedMessage(req));
        sut.Receive(new RequestSelectedMessage(req));

        sut.Tabs.Should().HaveCount(1);
    }

    // ─── Promoted tab is not replaced ─────────────────────────────────────────

    [Fact]
    public void PromotedTransientTab_IsNotClosedWhenNewTransientIsOpened()
    {
        var sut = BuildSut();
        var reqA = MakeRequest(@"/col/a.callsmith", "Alpha");
        var reqB = MakeRequest(@"/col/b.callsmith", "Beta");

        sut.Receive(new RequestSelectedMessage(reqA));
        var tabA = sut.Tabs[0];
        tabA.PromoteFromTransient(); // promoted → not transient any more

        sut.Receive(new RequestSelectedMessage(reqB));

        sut.Tabs.Should().HaveCount(2, "the promoted tab should remain open");
        sut.Tabs.Should().Contain(tabA);
        tabA.IsTransient.Should().BeFalse();
        sut.Tabs.Single(t => t != tabA).IsTransient.Should().BeTrue();
    }

    // ─── Promotion by double-click ────────────────────────────────────────────

    [Fact]
    public void PromoteFromTransient_SetsIsTransientToFalse()
    {
        var sut = BuildSut();
        sut.Receive(new RequestSelectedMessage(MakeRequest(@"/col/a.callsmith", "Alpha")));
        var tab = sut.Tabs[0];

        tab.PromoteFromTransient();

        tab.IsTransient.Should().BeFalse();
    }

    [Fact]
    public void PromoteFromTransient_OnNonTransientTab_DoesNothing()
    {
        var sut = BuildSut();
        sut.NewTab();
        var tab = sut.Tabs[0];

        // Should not throw
        tab.PromoteFromTransient();
        tab.IsTransient.Should().BeFalse();
    }

    // ─── Promotion by unsaved change ──────────────────────────────────────────

    [Fact]
    public void MakingChangesToTransientTab_PromotesItToNormalTab()
    {
        var sut = BuildSut();
        sut.Receive(new RequestSelectedMessage(MakeRequest(@"/col/a.callsmith", "Alpha")));
        var tab = sut.Tabs[0];

        // Simulate a user edit (set directly to mimic the property change fired by the VM).
        tab.HasUnsavedChanges = true;

        tab.IsTransient.Should().BeFalse("unsaved change should promote the transient tab");
    }

    [Fact]
    public void MakingChangeToPromotedTab_DoesNotCreateNewTransientOnSubsequentSidebarClick()
    {
        var sut = BuildSut();
        var reqA = MakeRequest(@"/col/a.callsmith", "Alpha");
        var reqB = MakeRequest(@"/col/b.callsmith", "Beta");

        sut.Receive(new RequestSelectedMessage(reqA));
        var tabA = sut.Tabs[0];
        tabA.HasUnsavedChanges = true; // promotes tabA

        sut.Receive(new RequestSelectedMessage(reqB));

        // tabA should still be open because it was promoted before reqB was clicked.
        sut.Tabs.Should().HaveCount(2);
        sut.Tabs.Should().Contain(tabA);
    }

    // ─── Mixed: permanent + transient tabs ───────────────────────────────────

    [Fact]
    public void NewTab_IsNotTransient()
    {
        var sut = BuildSut();
        sut.NewTab();

        sut.Tabs[0].IsTransient.Should().BeFalse();
    }

    [Fact]
    public void OpeningTransientTab_LeavesExistingNonTransientTabsUntouched()
    {
        var sut = BuildSut();
        sut.NewTab();
        var permanentTab = sut.Tabs[0];

        sut.Receive(new RequestSelectedMessage(MakeRequest(@"/col/a.callsmith", "Alpha")));

        sut.Tabs.Should().HaveCount(2);
        sut.Tabs.Should().Contain(permanentTab);
    }

    // ─── Command palette: open as permanent ──────────────────────────────────

    [Fact]
    public void ReceiveRequestSelected_OpenAsPermanent_OpensTabAsPermanent()
    {
        var sut = BuildSut();
        var req = MakeRequest(@"/col/a.callsmith", "Alpha");

        sut.Receive(new RequestSelectedMessage(req, openAsPermanent: true));

        sut.Tabs.Should().HaveCount(1);
        sut.Tabs[0].IsTransient.Should().BeFalse("command palette opens requests as permanent tabs");
    }

    [Fact]
    public void ReceiveRequestSelected_OpenAsPermanent_DoesNotReplaceExistingTransientTab()
    {
        var sut = BuildSut();
        var reqA = MakeRequest(@"/col/a.callsmith", "Alpha");
        var reqB = MakeRequest(@"/col/b.callsmith", "Beta");

        // Open a transient tab via sidebar click.
        sut.Receive(new RequestSelectedMessage(reqA));
        var transientTab = sut.Tabs[0];
        transientTab.IsTransient.Should().BeTrue();

        // Open a different request from the command palette.
        sut.Receive(new RequestSelectedMessage(reqB, openAsPermanent: true));

        sut.Tabs.Should().HaveCount(2, "the transient tab should NOT be displaced by a command palette open");
        sut.Tabs.Should().Contain(transientTab);
        transientTab.IsTransient.Should().BeTrue("the original transient tab should remain transient");
        sut.Tabs.Single(t => t != transientTab).IsTransient.Should().BeFalse("the command palette tab should be permanent");
    }

    [Fact]
    public void ReceiveRequestSelected_OpenAsPermanent_ForExistingTransient_PromotesTab()
    {
        var sut = BuildSut();
        var req = MakeRequest(@"/col/a.callsmith", "Alpha");

        sut.Receive(new RequestSelectedMessage(req));
        sut.Tabs.Should().HaveCount(1);
        sut.Tabs[0].IsTransient.Should().BeTrue();

        sut.Receive(new RequestSelectedMessage(req, openAsPermanent: true));

        sut.Tabs.Should().HaveCount(1, "opening the same request should focus/promote, not duplicate");
        sut.Tabs[0].IsTransient.Should().BeFalse("openAsPermanent should promote an existing transient tab");
    }

    [Fact]
    public void ReceiveRequestSelected_OpenAsPermanent_FocusesExistingTabIfAlreadyOpen()
    {
        var sut = BuildSut();
        var req = MakeRequest(@"/col/a.callsmith", "Alpha");

        // Open once via sidebar (transient), then via command palette.
        sut.Receive(new RequestSelectedMessage(req));
        sut.Receive(new RequestSelectedMessage(req, openAsPermanent: true));

        sut.Tabs.Should().HaveCount(1, "should focus the existing tab, not open a second one");
    }

    [Fact]
    public void OpeningTransientTab_AfterCommandPaletteOpen_DoesNotClosePermanentCommandPaletteTab()
    {
        var sut = BuildSut();
        var reqA = MakeRequest(@"/col/a.callsmith", "Alpha");
        var reqB = MakeRequest(@"/col/b.callsmith", "Beta");

        // Open from command palette first (permanent tab).
        sut.Receive(new RequestSelectedMessage(reqA, openAsPermanent: true));
        var permanentTab = sut.Tabs[0];

        // Then click a different request in the sidebar (transient tab).
        sut.Receive(new RequestSelectedMessage(reqB));

        sut.Tabs.Should().HaveCount(2, "the permanent command palette tab should remain open");
        sut.Tabs.Should().Contain(permanentTab);
        permanentTab.IsTransient.Should().BeFalse();
        sut.Tabs.Single(t => t != permanentTab).IsTransient.Should().BeTrue();
    }
}
