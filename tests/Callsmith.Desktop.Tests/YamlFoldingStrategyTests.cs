using AvaloniaEdit.Document;
using Callsmith.Desktop.Controls;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

public sealed class YamlFoldingStrategyTests
{
    [Fact]
    public void CreateNewFoldings_FoldsNestedMappings()
    {
        var document = new TextDocument(
            """
            root:
              service:
                host: localhost
                port: 8080
              enabled: true
            """);

        var strategy = new YamlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().HaveCount(2);
        foldings.Select(f => f.Name).Should().Contain(["root: ← 2 →", "service: ← 2 →"]);
    }

    [Fact]
    public void CreateNewFoldings_FoldsSequenceItemsWithNestedBlocks()
    {
        var document = new TextDocument(
            """
            jobs:
              - name: build
                steps:
                  - run: dotnet build
              - name: test
                steps:
                  - run: dotnet test
            """);

        var strategy = new YamlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Select(f => f.Name).Should().Contain("jobs: ← 2 →");
        foldings.Select(f => f.Name).Should().Contain("- ← 2 →");
        foldings.Select(f => f.Name).Should().Contain("steps: ← 1 →");
    }

    [Fact]
    public void CreateNewFoldings_CountsInlineMappingKeyOnSequenceItemOpener()
    {
        var document = new TextDocument(
            """
            subnets:
              - id: subnet-a1
                cidr: 10.0.1.0/24
                zones:
                  - us-east-1a
            """);

        var strategy = new YamlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Select(f => f.Name).Should().Contain("- ← 3 →");
        foldings.Select(f => f.Name).Should().Contain("zones: ← 1 →");
    }

    [Fact]
    public void CreateNewFoldings_DoesNotLetScalarSequenceItemAbsorbSiblingBlocks()
    {
        var document = new TextDocument(
            """
            features:
              - "login"
              - "dashboard"
              - "reports"
            endpoints:
              - path: "/api/v1"
                active: true
              - path: "/api/v2"
                active: false
            """);

        var strategy = new YamlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        var reportsLine = document.GetLineByNumber(4);
        foldings.Should().NotContain(f => f.StartOffset == reportsLine.Offset + 2);
    }

    [Fact]
    public void CreateNewFoldings_PreservesIndentationForNestedKeyFoldings()
    {
        var document = new TextDocument(
            """
            database:
              connection:
                options:
                  ssl: true
                  pool: 10
            """);

        var strategy = new YamlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        var optionsFolding = foldings.Single(f => f.Name == "options: ← 2 →");
        var optionsLine = document.GetLineByNumber(3);
        var optionsText = document.GetText(optionsLine.Offset, optionsLine.Length);
        var expectedStartOffset = optionsLine.Offset + optionsText.TakeWhile(c => c == ' ').Count();

        optionsFolding.StartOffset.Should().Be(expectedStartOffset);
    }

    [Fact]
    public void CreateNewFoldings_FoldsBlockScalars()
    {
        var document = new TextDocument(
            """
            description: |
              line one
              line two
            name: sample
            """);

        var strategy = new YamlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().ContainSingle(f => f.Name == "description: ← 0 →");
    }

    [Fact]
    public void CreateNewFoldings_DoesNotThrowForSparseOrCommentOnlyBody()
    {
        var document = new TextDocument(
            """
            # comment

            # another comment
            """);

        var strategy = new YamlFoldingStrategy();

        var action = () => strategy.CreateNewFoldings(document);

        action.Should().NotThrow();
    }
}
