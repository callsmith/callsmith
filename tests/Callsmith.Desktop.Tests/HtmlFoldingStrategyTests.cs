using AvaloniaEdit.Document;
using Callsmith.Desktop.Controls;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

public sealed class HtmlFoldingStrategyTests
{
    [Fact]
    public void CreateNewFoldings_FoldsNestedElementsAcrossLines()
    {
        var document = new TextDocument(
            """
            <html>
              <body>
                <div>
                  <p>hello</p>
                </div>
              </body>
            </html>
            """);

        var strategy = new HtmlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().HaveCount(3);
        foldings.Select(f => f.Name).Should().Contain(["<html>...</html>", "<body>...</body>", "<div>...</div>"]);
    }

    [Fact]
    public void CreateNewFoldings_FoldsMultiLineComments()
    {
        var document = new TextDocument(
            """
            <div>
              <!--
                keep this collapsed
              -->
              <span>ok</span>
            </div>
            """);

        var strategy = new HtmlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Select(f => f.Name).Should().Contain(["<!--...-->", "<div>...</div>"]);
    }

    [Fact]
    public void CreateNewFoldings_DoesNotFoldVoidOrSelfClosingTags()
    {
        var document = new TextDocument(
            """
            <section>
              <img src="x.png" />
              <br>
              <input type="text">
            </section>
            """);

        var strategy = new HtmlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Should().ContainSingle(f => f.Name == "<section>...</section>");
    }

    [Fact]
    public void CreateNewFoldings_DoesNotThrowOnMismatchedTags()
    {
        var document = new TextDocument(
            """
            <div>
              <span>
            </div>
            """);

        var strategy = new HtmlFoldingStrategy();

        var action = () => strategy.CreateNewFoldings(document);

        action.Should().NotThrow();
    }
}
