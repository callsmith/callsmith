using Callsmith.Desktop.ViewModels;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

public sealed class AdvancedHistorySearchViewModelTests
{
    [Fact]
    public void ActiveFilterCount_IsZero_WhenNoFieldsSet()
    {
        var sut = new AdvancedHistorySearchViewModel();
        sut.ActiveFilterCount.Should().Be(0);
    }

    [Fact]
    public void ActiveFilterCount_IncrementsForEachDistinctFilterGroup()
    {
        var sut = new AdvancedHistorySearchViewModel();

        sut.RequestContains = "hello";
        sut.ActiveFilterCount.Should().Be(1);

        sut.ResponseContains = "world";
        sut.ActiveFilterCount.Should().Be(2);

        sut.MethodSearch = "GET";
        sut.ActiveFilterCount.Should().Be(3);

        sut.MinStatusCode = 200;
        sut.ActiveFilterCount.Should().Be(4);

        // MaxStatusCode shares the same "Status code" bucket — should not add again
        sut.MaxStatusCode = 299;
        sut.ActiveFilterCount.Should().Be(4);

        sut.DateFromDate = DateTimeOffset.UtcNow;
        sut.ActiveFilterCount.Should().Be(5);

        // DateToDate shares the same "Date range" bucket
        sut.DateToDate = DateTimeOffset.UtcNow;
        sut.ActiveFilterCount.Should().Be(5);

        sut.MinElapsedMs = 10;
        sut.ActiveFilterCount.Should().Be(6);

        // MaxElapsedMs shares the same "Elapsed" bucket
        sut.MaxElapsedMs = 5000;
        sut.ActiveFilterCount.Should().Be(6);
    }

    [Fact]
    public void ClearAllCommand_ResetsAllFieldsAndCountToZero()
    {
        var sut = new AdvancedHistorySearchViewModel();
        sut.RequestContains = "foo";
        sut.ResponseContains = "bar";
        sut.MethodSearch = "POST";
        sut.MinStatusCode = 400;
        sut.MaxStatusCode = 499;
        sut.DateFromDate = DateTimeOffset.UtcNow.AddDays(-1);
        sut.DateToDate = DateTimeOffset.UtcNow;
        sut.MinElapsedMs = 50;
        sut.MaxElapsedMs = 2000;

        sut.ClearAllCommand.Execute(null);

        sut.RequestContains.Should().BeEmpty();
        sut.ResponseContains.Should().BeEmpty();
        sut.MethodSearch.Should().BeEmpty();
        sut.MinStatusCode.Should().BeNull();
        sut.MaxStatusCode.Should().BeNull();
        sut.DateFromDate.Should().BeNull();
        sut.DateToDate.Should().BeNull();
        sut.MinElapsedMs.Should().BeNull();
        sut.MaxElapsedMs.Should().BeNull();
        sut.ActiveFilterCount.Should().Be(0);
    }

    [Fact]
    public void ApplyCommand_RaisesAppliedEvent()
    {
        var sut = new AdvancedHistorySearchViewModel();
        var raised = false;
        sut.Applied += (_, _) => raised = true;

        sut.ApplyCommand.Execute(null);

        raised.Should().BeTrue();
    }

    [Fact]
    public void CancelCommand_RaisesCancelledEvent()
    {
        var sut = new AdvancedHistorySearchViewModel();
        var raised = false;
        sut.Cancelled += (_, _) => raised = true;

        sut.CancelCommand.Execute(null);

        raised.Should().BeTrue();
    }

    [Fact]
    public void ClearAllCommand_DoesNotRaiseAppliedEvent()
    {
        var sut = new AdvancedHistorySearchViewModel();
        var raised = false;
        sut.Applied += (_, _) => raised = true;

        sut.ClearAllCommand.Execute(null);

        raised.Should().BeFalse();
    }

    // SentAfter / SentBefore combination tests

    [Fact]
    public void SentAfter_IsNull_WhenDateFromDateNotSet()
    {
        var sut = new AdvancedHistorySearchViewModel();
        sut.SentAfter.Should().BeNull();
    }

    [Fact]
    public void SentAfter_DefaultsToMidnight_WhenOnlyDateSet()
    {
        var sut = new AdvancedHistorySearchViewModel();
        var today = DateTimeOffset.Now;
        sut.DateFromDate = today;

        sut.SentAfter.Should().NotBeNull();
        sut.SentAfter!.Value.TimeOfDay.Should().Be(TimeSpan.Zero);
        sut.SentAfter.Value.Date.Should().Be(today.Date);
    }

    [Fact]
    public void SentAfter_IncludesTime_WhenBothDateAndTimeSet()
    {
        var sut = new AdvancedHistorySearchViewModel();
        var today = DateTimeOffset.Now;
        var time = new TimeSpan(9, 30, 0);

        sut.DateFromDate = today;
        sut.DateFromTime = time;

        sut.SentAfter!.Value.TimeOfDay.Should().Be(time);
    }

    [Fact]
    public void SentBefore_DefaultsToEndOfDay_WhenOnlyDateSet()
    {
        var sut = new AdvancedHistorySearchViewModel();
        var today = DateTimeOffset.Now;
        sut.DateToDate = today;

        sut.SentBefore.Should().NotBeNull();
        sut.SentBefore!.Value.TimeOfDay.Should().Be(new TimeSpan(23, 59, 59));
    }

    [Fact]
    public void SentBefore_IsNull_WhenDateToDateNotSet()
    {
        var sut = new AdvancedHistorySearchViewModel();
        sut.SentBefore.Should().BeNull();
    }
}
