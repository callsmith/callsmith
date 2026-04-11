using Callsmith.Core.Helpers;
using FluentAssertions;

namespace Callsmith.Core.Tests.Helpers;

public sealed class AppDataPathsTests
{
    [Fact]
    public void GetCallsmithAppDataDirectory_ReturnsNonEmptyPath()
    {
        var dir = AppDataPaths.GetCallsmithAppDataDirectory();

        dir.Should().NotBeNullOrWhiteSpace();
        dir.Should().EndWith("Callsmith");
    }

    [Fact]
    public void GetCallsmithAppDataDirectory_CreatesDirectory()
    {
        var dir = AppDataPaths.GetCallsmithAppDataDirectory();

        Directory.Exists(dir).Should().BeTrue(
            "GetCallsmithAppDataDirectory must create the directory if it does not exist");
    }
}
