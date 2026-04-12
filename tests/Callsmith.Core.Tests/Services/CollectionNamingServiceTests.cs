using Callsmith.Core.Services;
using Callsmith.Core.Tests.TestHelpers;
using FluentAssertions;

namespace Callsmith.Core.Tests.Services;

public sealed class CollectionNamingServiceTests
{
    [Fact]
    public async Task PickUniqueRequestNameAsync_AppendsCounterWhenNameExists()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateSubDirectory("requests");

        File.WriteAllText(Path.Combine(folder, "New Request.callsmith"), "{}");
        File.WriteAllText(Path.Combine(folder, "New Request 2.callsmith"), "{}");

        var sut = new CollectionNamingService();

        var uniqueName = await sut.PickUniqueRequestNameAsync(folder, "New Request", ".callsmith");

        uniqueName.Should().Be("New Request 3");
    }

    [Fact]
    public async Task PickUniqueFolderNameAsync_AppendsCounterWhenFolderExists()
    {
        using var temp = new TempDirectory();
        var parent = temp.CreateSubDirectory("root");

        Directory.CreateDirectory(Path.Combine(parent, "New Folder"));
        Directory.CreateDirectory(Path.Combine(parent, "New Folder 2"));

        var sut = new CollectionNamingService();

        var uniqueName = await sut.PickUniqueFolderNameAsync(parent, "New Folder");

        uniqueName.Should().Be("New Folder 3");
    }
}
