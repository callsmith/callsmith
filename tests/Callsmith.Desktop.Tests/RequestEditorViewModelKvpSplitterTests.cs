using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class RequestEditorViewModelKvpSplitterTests
{
    private static (RequestEditorViewModel Sut, IAppPreferencesService AppPreferencesService) BuildSut()
    {
        var messenger = new WeakReferenceMessenger();
        var collectionService = Substitute.For<ICollectionService>();
        collectionService.RequestFileExtension.Returns(".callsmith");

        var preferencesService = Substitute.For<ICollectionPreferencesService>();
        var transportRegistry = Substitute.For<ITransportRegistry>();
        var dynamicEvaluator = Substitute.For<IDynamicVariableEvaluator>();
        var appPreferencesService = Substitute.For<IAppPreferencesService>();

        var sut = new RequestEditorViewModel(
            transportRegistry,
            collectionService,
            preferencesService,
            dynamicEvaluator,
            new EnvironmentMergeService(dynamicEvaluator),
            messenger,
            NullLogger<RequestEditorViewModel>.Instance,
            appPreferencesService: appPreferencesService);

        return (sut, appPreferencesService);
    }

    [Fact]
    public async Task QueryParamsSplitterChanged_SynchronizesAcrossTabs_AndPersistsAppPreference()
    {
        var (sut, appPreferencesService) = BuildSut();
        AppPreferences? persisted = null;
        var persistedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        appPreferencesService
            .UpdateAsync(Arg.Any<Func<AppPreferences, AppPreferences>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var update = callInfo.Arg<Func<AppPreferences, AppPreferences>>();
                persisted = update(new AppPreferences());
                persistedTcs.TrySetResult(true);
                return Task.CompletedTask;
            });

        sut.NewTab();
        sut.NewTab();

        var sourceTab = sut.Tabs[0];
        sourceTab.QueryParams.SplitterChangedCallback.Should().NotBeNull();

        sourceTab.QueryParams.SplitterChangedCallback!.Invoke(0.72);
        await persistedTcs.Task;

        sut.Tabs.Should().OnlyContain(t => t.QueryParams.KeyValueSplitterFraction == 0.72);
        persisted.Should().NotBeNull();
        persisted!.QueryParamsKvpSplitterFraction.Should().Be(0.72);
    }
}
