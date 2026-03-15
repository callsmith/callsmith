using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="RecentCollectionsService"/>.
/// Each test uses a <see cref="RecentCollectionsService"/> constructed with an
/// isolated temporary directory so no tests share state or write to %APPDATA%.
/// </summary>
public sealed class RecentCollectionsServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecentCollectionsService _sut;

    public RecentCollectionsServiceTests()
    {
        _sut = new RecentCollectionsService(
            _temp.Path,
            NullLogger<RecentCollectionsService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    // ─── LoadAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsEmptyList()
    {
        var result = await _sut.LoadAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_FiltersOutPathsThatNoLongerExistOnDisk()
    {
        var existing = _temp.CreateSubDirectory("valid-collection");
        var missing = Path.Combine(_temp.Path, "gone-collection");

        await _sut.PushAsync(existing);
        await _sut.PushAsync(missing);

        var result = await _sut.LoadAsync();

        result.Should().ContainSingle().Which.Should().Be(existing);
    }

    // ─── PushAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_NewPath_AppearsFirstInList()
    {
        var first = _temp.CreateSubDirectory("first");
        var second = _temp.CreateSubDirectory("second");

        await _sut.PushAsync(first);
        await _sut.PushAsync(second);

        var result = await _sut.LoadAsync();

        result[0].Should().Be(second);
        result[1].Should().Be(first);
    }

    [Fact]
    public async Task PushAsync_DuplicatePath_DeduplicatesCaseInsensitively()
    {
        var path = _temp.CreateSubDirectory("my-collection");
        var pathAlt = path.ToUpperInvariant();

        await _sut.PushAsync(path);
        await _sut.PushAsync(pathAlt);

        var result = await _sut.LoadAsync();

        result.Should().ContainSingle();
    }

    [Fact]
    public async Task PushAsync_ExistingPath_MovesItToFront()
    {
        var col1 = _temp.CreateSubDirectory("col1");
        var col2 = _temp.CreateSubDirectory("col2");
        var col3 = _temp.CreateSubDirectory("col3");

        await _sut.PushAsync(col1);
        await _sut.PushAsync(col2);
        await _sut.PushAsync(col3);
        // Push col1 again — should move to front
        await _sut.PushAsync(col1);

        var result = await _sut.LoadAsync();

        result[0].Should().Be(col1);
        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task PushAsync_ExceedsMaxEntries_TrimsOldestEntries()
    {
        // Create 11 unique directories (one more than the max of 10).
        var dirs = Enumerable.Range(1, 11)
            .Select(i => _temp.CreateSubDirectory($"col{i:D2}"))
            .ToList();

        foreach (var dir in dirs)
            await _sut.PushAsync(dir);

        var result = await _sut.LoadAsync();

        result.Should().HaveCount(10);
        // The oldest entry (dirs[0]) should have been dropped.
        result.Should().NotContain(dirs[0]);
        // The most recent entry should be first.
        result[0].Should().Be(dirs[10]);
    }

    [Fact]
    public async Task PushAsync_ArgumentNull_ThrowsArgumentNullException()
    {
        var act = () => _sut.PushAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PushAndLoad_RoundTrip_PreservesOrder()
    {
        var col1 = _temp.CreateSubDirectory("alpha");
        var col2 = _temp.CreateSubDirectory("beta");
        var col3 = _temp.CreateSubDirectory("gamma");

        await _sut.PushAsync(col1);
        await _sut.PushAsync(col2);
        await _sut.PushAsync(col3);

        var result = await _sut.LoadAsync();

        result.Should().ContainInOrder(col3, col2, col1);
    }
}
