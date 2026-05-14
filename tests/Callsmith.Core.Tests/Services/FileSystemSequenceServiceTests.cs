using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="FileSystemSequenceService"/>.
/// Each test gets its own isolated temporary directory.
/// </summary>
public sealed class FileSystemSequenceServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileSystemSequenceService _sut =
        new(NullLogger<FileSystemSequenceService>.Instance);

    public void Dispose() => _temp.Dispose();

    // ─── ListSequencesAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ListSequencesAsync_WhenSequencesFolderMissing_ReturnsEmptyList()
    {
        var collection = _temp.CreateSubDirectory("col");

        var result = await _sut.ListSequencesAsync(collection);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSequencesAsync_ReturnsSortedByName()
    {
        var collection = _temp.CreateSubDirectory("col");
        await _sut.CreateSequenceAsync(collection, "Zoo");
        await _sut.CreateSequenceAsync(collection, "Alpha");
        await _sut.CreateSequenceAsync(collection, "Middle");

        var result = await _sut.ListSequencesAsync(collection);

        result.Select(s => s.Name).Should().ContainInOrder("Alpha", "Middle", "Zoo");
    }

    [Fact]
    public async Task ListSequencesAsync_IgnoresUnreadableFiles()
    {
        var collection = _temp.CreateSubDirectory("col");
        await _sut.CreateSequenceAsync(collection, "Valid");

        var folder = Path.Combine(collection, FileSystemSequenceService.SequencesFolderName);
        File.WriteAllText(
            Path.Combine(folder, "bad.seq.callsmith"),
            "{ not valid json }");

        var result = await _sut.ListSequencesAsync(collection);

        result.Should().HaveCount(1).And.Contain(s => s.Name == "Valid");
    }

    // ─── CreateSequenceAsync / LoadSequenceAsync ─────────────────────────────

    [Fact]
    public async Task CreateSequenceAsync_CreatesFileWithCorrectName()
    {
        var collection = _temp.CreateSubDirectory("col");

        var seq = await _sut.CreateSequenceAsync(collection, "Login Flow");

        seq.Name.Should().Be("Login Flow");
        seq.SequenceId.Should().NotBeEmpty();
        seq.Steps.Should().BeEmpty();
        File.Exists(seq.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateSequenceAsync_FileExtensionIsCorrect()
    {
        var collection = _temp.CreateSubDirectory("col");

        var seq = await _sut.CreateSequenceAsync(collection, "My Sequence");

        seq.FilePath.Should().EndWith(FileSystemSequenceService.SequenceFileExtension);
        seq.FilePath.Should().Contain(".seq.callsmith");
    }

    [Fact]
    public async Task CreateSequenceAsync_ThrowsWhenNameAlreadyExists()
    {
        var collection = _temp.CreateSubDirectory("col");
        await _sut.CreateSequenceAsync(collection, "Dupe");

        var act = async () => await _sut.CreateSequenceAsync(collection, "Dupe");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LoadSequenceAsync_RoundTripsStepsAndExtractions()
    {
        var collection = _temp.CreateSubDirectory("col");
        var original = await _sut.CreateSequenceAsync(collection, "Full Sequence");

        var updated = original with
        {
            Steps =
            [
                new SequenceStep
                {
                    StepId = Guid.NewGuid(),
                    RequestFilePath = "/col/Login.callsmith",
                    RequestName = "Login",
                    Extractions =
                    [
                        new VariableExtraction
                        {
                            VariableName = "token",
                            Source = VariableExtractionSource.ResponseBody,
                            Expression = "$.access_token",
                        },
                    ],
                },
            ],
        };

        await _sut.SaveSequenceAsync(updated);
        var loaded = await _sut.LoadSequenceAsync(updated.FilePath);

        loaded.Name.Should().Be("Full Sequence");
        loaded.Steps.Should().HaveCount(1);
        loaded.Steps[0].RequestName.Should().Be("Login");
        loaded.Steps[0].Extractions.Should().HaveCount(1);
        loaded.Steps[0].Extractions[0].VariableName.Should().Be("token");
        loaded.Steps[0].Extractions[0].Source.Should().Be(VariableExtractionSource.ResponseBody);
        loaded.Steps[0].Extractions[0].Expression.Should().Be("$.access_token");
    }

    [Fact]
    public async Task LoadSequenceAsync_ThrowsWhenFileNotFound()
    {
        var act = async () =>
            await _sut.LoadSequenceAsync("/nonexistent/path.seq.callsmith");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ─── DeleteSequenceAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSequenceAsync_RemovesFile()
    {
        var collection = _temp.CreateSubDirectory("col");
        var seq = await _sut.CreateSequenceAsync(collection, "ToDelete");

        await _sut.DeleteSequenceAsync(seq.FilePath);

        File.Exists(seq.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteSequenceAsync_IsIdempotentWhenFileNotFound()
    {
        var act = async () =>
            await _sut.DeleteSequenceAsync("/nonexistent/path.seq.callsmith");

        await act.Should().NotThrowAsync();
    }

    // ─── RenameSequenceAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task RenameSequenceAsync_UpdatesNameAndFilePath()
    {
        var collection = _temp.CreateSubDirectory("col");
        var seq = await _sut.CreateSequenceAsync(collection, "OldName");

        var renamed = await _sut.RenameSequenceAsync(seq.FilePath, "NewName");

        renamed.Name.Should().Be("NewName");
        renamed.FilePath.Should().EndWith("NewName.seq.callsmith");
        File.Exists(renamed.FilePath).Should().BeTrue();
        File.Exists(seq.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task RenameSequenceAsync_PreservesSteps()
    {
        var collection = _temp.CreateSubDirectory("col");
        var seq = await _sut.CreateSequenceAsync(collection, "OriginalSeq");
        var withSteps = seq with
        {
            Steps =
            [
                new SequenceStep
                {
                    StepId = Guid.NewGuid(),
                    RequestFilePath = "/col/Step1.callsmith",
                    RequestName = "Step 1",
                },
            ],
        };
        await _sut.SaveSequenceAsync(withSteps);

        var renamed = await _sut.RenameSequenceAsync(withSteps.FilePath, "RenamedSeq");

        renamed.Steps.Should().HaveCount(1);
        renamed.Steps[0].RequestName.Should().Be("Step 1");
    }
}
