using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Callsmith.Desktop.Tests;

public sealed class SequenceEditorViewModelTests
{
    private static SequenceEditorViewModel CreateSut() =>
        new(
            Substitute.For<ISequenceService>(),
            Substitute.For<ISequenceRunnerService>(),
            NullLogger<SequenceEditorViewModel>.Instance);

    private static SequenceModel CreateSequence(params SequenceStep[] steps) => new()
    {
        SequenceId = Guid.NewGuid(),
        FilePath = "/tmp/sequence.seq.callsmith",
        Name = "Sequence",
        Steps = steps,
    };

    private static SequenceStep CreateStep(string name) => new()
    {
        StepId = Guid.NewGuid(),
        RequestFilePath = $"/tmp/{name}.callsmith",
        RequestName = name,
        Extractions = [],
    };

    [Fact]
    public void MoveStep_WhenDestinationChanges_ReordersStepsAndMarksDirty()
    {
        var sut = CreateSut();
        sut.LoadSequence(CreateSequence(CreateStep("A"), CreateStep("B"), CreateStep("C")));

        var stepA = sut.Steps[0];
        sut.MoveStep(stepA, 2);

        sut.Steps.Select(s => s.RequestName).Should().ContainInOrder("B", "C", "A");
        sut.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void MoveStep_WhenDestinationIsSame_DoesNothing()
    {
        var sut = CreateSut();
        sut.LoadSequence(CreateSequence(CreateStep("A"), CreateStep("B")));
        var stepA = sut.Steps[0];

        sut.MoveStep(stepA, 0);

        sut.Steps.Select(s => s.RequestName).Should().ContainInOrder("A", "B");
        sut.IsDirty.Should().BeFalse();
    }
}

