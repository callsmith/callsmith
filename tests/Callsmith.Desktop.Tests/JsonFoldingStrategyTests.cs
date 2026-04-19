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
        foldings.Select(f => f.Name).Should().Contain(["{← 1 →}", "[← 2 →]", "{← 1 →}"]);
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
        foldings.Select(f => f.Name).Should().Contain(["{← 2 →}", "[← 2 →]"]);
    }

    [Fact]
    public void CreateNewFoldings_CountsOnlyImmediateChildrenForNestedContainers()
    {
        var document = new TextDocument(
            """
            {
              "meta": {
                "id": 7,
                "tags": [
                  "a",
                  "b"
                ]
              },
              "items": [
                {
                  "name": "alpha",
                  "values": [
                    1,
                    2,
                    3
                  ]
                },
                {
                  "name": "beta",
                  "values": []
                }
              ]
            }
            """);

        var strategy = new JsonFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().Contain(f => f.Name == "{← 2 →}");
        foldings.Should().Contain(f => f.Name == "[← 2 →]");
        foldings.Should().Contain(f => f.Name == "[← 3 →]");
    }

    [Fact]
    public void CreateNewFoldings_CountsEmptyContainersAsZero()
    {
        var document = new TextDocument(
            """
            {
              "emptyObject": {
              },
              "emptyArray": [
              ],
              "singleArray": [
                1
              ],
              "singleObject": {
                "x": 1
              }
            }
            """);

        var strategy = new JsonFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Select(f => f.Name).Should().Contain([
            "{← 4 →}",
            "{← 0 →}",
            "[← 0 →]",
            "[← 1 →]",
            "{← 1 →}",
        ]);
    }

    [Fact]
    public void CreateNewFoldings_StringContainingOnlyBracesDoesNotCreateFolding()
    {
        // The string value contains { and } but they must not be treated as folding delimiters.
        var document = new TextDocument(
            """
            {
              "template": "Hello {name}, welcome to {place}!"
            }
            """);

        var strategy = new JsonFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().HaveCount(1);
        foldings[0].Name.Should().Be("{← 1 →}");
    }

    [Fact]
    public void CreateNewFoldings_StringContainingOnlyBracketsDoesNotCreateFolding()
    {
        // The string value contains [ and ] but they must not be treated as folding delimiters.
        var document = new TextDocument(
            """
            {
              "note": "see sections [1] and [2] for details"
            }
            """);

        var strategy = new JsonFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().HaveCount(1);
        foldings[0].Name.Should().Be("{← 1 →}");
    }

    [Fact]
    public void CreateNewFoldings_StringDelimitersDoNotInflateChildCount()
    {
        // String values contain { }, [ ], and : characters; none should affect folding counts.
        var document = new TextDocument(
            """
            {
              "a": "value: {nested: [1, 2]}",
              "b": "another: [x, y]"
            }
            """);

        var strategy = new JsonFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        // Only the outer object should fold; its two real properties give a count of 2.
        foldings.Should().HaveCount(1);
        foldings[0].Name.Should().Be("{← 2 →}");
    }

    [Fact]
    public void CreateNewFoldings_EscapedQuoteInsideStringDoesNotEndString()
    {
        // An escaped quote inside a string must not prematurely end the string context,
        // allowing the subsequent { or [ to be mistakenly treated as a folding opener.
        var document = new TextDocument(
            """
            {
              "msg": "say \"hello {world}\" here",
              "ok": true
            }
            """);

        var strategy = new JsonFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().HaveCount(1);
        foldings[0].Name.Should().Be("{← 2 →}");
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
