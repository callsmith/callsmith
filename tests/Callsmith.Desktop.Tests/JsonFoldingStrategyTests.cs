using AvaloniaEdit.Document;
using Callsmith.Desktop.Controls;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

public sealed class JsonFoldingStrategyTests
{
    [Fact]
    public void CreateNewFoldings_FoldsObjectsAndArraysAcrossLines()
    {
        var document = new TextDocument(
            """
            {
              "meta": {
                "ids": [
                  1,
                  2
                ]
              }
            }
            """);

        var strategy = new JsonFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().HaveCount(3);
        foldings.Select(f => f.Name).Should().Contain(["{...}", "[...]", "{...}"]);
    }

    [Fact]
    public void CreateNewFoldings_IgnoresDelimitersInsideStrings()
    {
        var document = new TextDocument(
            """
            {
              "text": "this { should [ not ] fold }",
              "ids": [
                1,
                2
              ]
            }
            """);

        var strategy = new JsonFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().HaveCount(2);
        foldings.Select(f => f.Name).Should().Contain(["{...}", "[...]"]);
    }

    [Fact]
    public void CreateNewFoldings_DoesNotThrowOnMismatchedDelimiters()
    {
        var document = new TextDocument(
            """
            {
              "items": [
                { "id": 1 }
              }
            ]
            """);

        var strategy = new JsonFoldingStrategy();

        var action = () => strategy.CreateNewFoldings(document);

        action.Should().NotThrow();
    }
}
