using FluentAssertions;

namespace Callsmith.Data.Tests;

public sealed class CallsmithDbContextTests
{
    [Fact]
    public void GetDbPath_NormalizesCaseAndTrailingSeparator()
    {
        using var temp = new TestHelpers.TempDirectory();
        var basePath = temp.CreateSubDirectory("CollectionRoot");
        var samePathDifferentForm = basePath.ToUpperInvariant() + Path.DirectorySeparatorChar;

        var left = CallsmithDbContext.GetDbPath(basePath);
        var right = CallsmithDbContext.GetDbPath(samePathDifferentForm);

        left.Should().Be(right);
    }

    [Fact]
    public void GetDbPath_UsesHashedDbFileNameUnderHistoryFolder()
    {
        using var temp = new TestHelpers.TempDirectory();
        var collectionPath = temp.CreateSubDirectory("orders");

        var dbPath = CallsmithDbContext.GetDbPath(collectionPath);
        var fileName = Path.GetFileName(dbPath);

        dbPath.Should().Contain(Path.Combine("Callsmith", "history"));
        fileName.Should().MatchRegex("^[a-f0-9]{64}\\.db$");
    }

    [Fact]
    public void GetDbPath_DifferentCollectionPaths_ProduceDifferentFiles()
    {
        using var temp = new TestHelpers.TempDirectory();
        var pathA = temp.CreateSubDirectory("collection-a");
        var pathB = temp.CreateSubDirectory("collection-b");

        var dbA = CallsmithDbContext.GetDbPath(pathA);
        var dbB = CallsmithDbContext.GetDbPath(pathB);

        Path.GetFileName(dbA).Should().NotBe(Path.GetFileName(dbB));
    }

    [Fact]
    public void GetKeyPath_IsStoredUnderCallsmithRoot()
    {
        var keyPath = GetKeyPath();

        keyPath.Should().EndWith(Path.Combine("Callsmith", "history.key"));
    }

    private static string GetKeyPath()
    {
        var method = typeof(CallsmithDbContext).GetMethod(
            "GetKeyPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull();
        return (string)method!.Invoke(null, null)!;
    }
}
