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
        foldings.Select(f => f.Name).Should().Contain(["<html>← 1 →</html>", "<body>← 1 →</body>", "<div>← 1 →</div>"]);
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

        foldings.Select(f => f.Name).Should().Contain(["<!--...-->", "<div>← 1 →</div>"]);
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

        foldings.Should().ContainSingle(f => f.Name == "<section>← 3 →</section>");
    }

    [Fact]
    public void CreateNewFoldings_CountsOnlyDirectChildElements()
    {
        var document = new TextDocument(
            """
            <main>
              <section>
                <div>
                  <p>hello</p>
                </div>
              </section>
              <aside>
                <span>note</span>
              </aside>
            </main>
            """);

        var strategy = new HtmlFoldingStrategy();

        var foldings = strategy.CreateNewFoldings(document);

        foldings.Select(f => f.Name).Should().Contain(["<main>← 2 →</main>", "<section>← 1 →</section>", "<div>← 1 →</div>", "<aside>← 1 →</aside>"]);
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
