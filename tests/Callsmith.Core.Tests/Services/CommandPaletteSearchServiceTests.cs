using System.Net.Http;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using FluentAssertions;

namespace Callsmith.Core.Tests.Services;

public class CommandPaletteSearchServiceTests
{
    private readonly CommandPaletteSearchService _sut = new();

    [Fact]
    public void FlattenRequests_FlattensFolderTree_WithDisplayPath()
    {
        var request = new CollectionRequest
        {
            FilePath = "req.json",
            Name = "FindByRoles",
            Method = HttpMethod.Get,
            Url = "https://example.test/users",
        };

        var roots = new[]
        {
            new CommandPaletteSearchNode
            {
                Name = "root",
                IsRoot = true,
                IsFolder = true,
                Children =
                [
                    new CommandPaletteSearchNode
                    {
                        Name = "Admin",
                        IsRoot = false,
                        IsFolder = true,
                        Children =
                        [
                            new CommandPaletteSearchNode
                            {
                                Name = "FindByRoles",
                                IsRoot = false,
                                IsFolder = false,
                                Request = request,
                            },
                        ],
                    },
                ],
            },
        };

        var entries = _sut.FlattenRequests(roots);

        entries.Should().HaveCount(1);
        entries[0].DisplayPath.Should().Be("Admin / FindByRoles");
        entries[0].MethodName.Should().Be("GET");
    }

    [Fact]
    public void Filter_MatchesQueryIgnoringSpacesDashesAndUnderscores()
    {
        var request = new CollectionRequest
        {
            FilePath = "req.json",
            Name = "findByRoles",
            Method = HttpMethod.Get,
            Url = "https://example.test/users",
        };

        var entries = new[]
        {
            new CommandPaletteSearchEntry(request, "findByRoles", "GET"),
        };

        var matches = _sut.Filter(entries, "find by-roles");

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Filter_MatchesUrlIgnoringSpacesDashesAndUnderscores()
    {
        var request = new CollectionRequest
        {
            FilePath = "req.json",
            Name = "findById",
            Method = HttpMethod.Get,
            Url = "https://example.test/api-service/user?id={{id}}",
        };

        var entries = new[]
        {
            new CommandPaletteSearchEntry(request, "findById", "GET"),
        };

        var matches = _sut.Filter(entries, "apiservice/user");

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Filter_WithEmptyQuery_ReturnsAllEntries()
    {
        var requestA = new CollectionRequest
        {
            FilePath = "a.json",
            Name = "ListUsers",
            Method = HttpMethod.Get,
            Url = "https://example.test/users",
        };
        var requestB = new CollectionRequest
        {
            FilePath = "b.json",
            Name = "CreateUser",
            Method = HttpMethod.Post,
            Url = "https://example.test/users",
        };

        var entries = new[]
        {
            new CommandPaletteSearchEntry(requestA, "ListUsers", "GET"),
            new CommandPaletteSearchEntry(requestB, "CreateUser", "POST"),
        };

        var matches = _sut.Filter(entries, string.Empty);

        matches.Should().HaveCount(2);
    }
}
