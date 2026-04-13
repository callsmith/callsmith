using Callsmith.Desktop.Controls;
using FluentAssertions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Callsmith.Desktop.Tests;

public sealed class SyntaxPathFilterTests
{
    [Fact]
    public void TryTransform_JsonPath_ExtractsScalar()
    {
        const string json = """
            {
              "data": {
                "items": [
                  { "id": 42, "name": "first" }
                ]
              }
            }
            """;

        var ok = SyntaxPathFilter.TryTransform(json, "json", "$.data.items[0].id", out var transformed, out var error);

        ok.Should().BeTrue();
        transformed.Should().Be("42");
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryTransform_JsonPath_InvalidPath_ReturnsError()
    {
        const string json = """{ "data": [1,2,3] }""";

        var ok = SyntaxPathFilter.TryTransform(json, "json", "data[0]", out var transformed, out var error);

        ok.Should().BeFalse();
        transformed.Should().Be(json);
        error.Should().Contain("must start with '$'");
    }

    [Fact]
    public void TryTransform_JsonPath_WildcardArraySelector_ExtractsAllValues()
    {
        const string json = """
            {
              "library": {
                "books": [
                  { "author": "Le Guin" },
                  { "author": "Butler" },
                  { "author": "Leckie" }
                ]
              }
            }
            """;

        var ok = SyntaxPathFilter.TryTransform(json, "json", "$.library.books[*].author", out var transformed, out var error);

        ok.Should().BeTrue();
        transformed.Should().Be("""
            [
              "Le Guin",
              "Butler",
              "Leckie"
            ]
            """);
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryTransform_JsonPath_WildcardArraySelector_OnEmptyArray_ReturnsEmpty()
    {
        const string json = """{ "library": { "books": [] } }""";

        var ok = SyntaxPathFilter.TryTransform(json, "json", "$.library.books[*].author", out var transformed, out var error);

        ok.Should().BeTrue();
        transformed.Should().BeEmpty();
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryTransform_XPath_ExtractsNodeValue()
    {
        const string xml = """
            <root>
              <users>
                <user>
                  <name>Ada</name>
                </user>
              </users>
            </root>
            """;

        var ok = SyntaxPathFilter.TryTransform(xml, "xml", "/root/users/user/name", out var transformed, out var error);

        ok.Should().BeTrue();
        transformed.Should().Be("Ada");
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryTransform_XPath_InvalidExpression_ReturnsError()
    {
        const string xml = """<root><value>1</value></root>""";

        var ok = SyntaxPathFilter.TryTransform(xml, "xml", "/root/[", out var transformed, out var error);

        ok.Should().BeFalse();
        transformed.Should().Be(xml);
        error.Should().StartWith("Invalid XPath");
    }

      [AvaloniaFact]
      public void FilteredSyntaxViewer_InvalidFilter_DoesNotShiftEditorPosition()
      {
        var viewer = new FilteredSyntaxViewer
        {
          Width = 640,
          Height = 320,
          Language = "json",
          Text = """{ \"data\": [1, 2, 3] }""",
        };

        var window = new Window
        {
          Width = 800,
          Height = 480,
          Content = viewer,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var editor = viewer.FindControl<SyntaxEditor>("Editor");
        var filterTextBox = viewer.FindControl<TextBox>("FilterTextBox");
    var filterStatusTextBlock = viewer.FindControl<TextBlock>("FilterStatusTextBlock");

        editor.Should().NotBeNull();
        filterTextBox.Should().NotBeNull();
    filterStatusTextBlock.Should().NotBeNull();

        var editorTopBefore = editor!.Bounds.Y;

        filterTextBox!.Text = "data[0]";
        Dispatcher.UIThread.RunJobs();

    filterStatusTextBlock!.IsVisible.Should().BeTrue();
    filterStatusTextBlock.Text.Should().Contain("must start with '$'");
        editor.Bounds.Y.Should().Be(editorTopBefore);
      }
}