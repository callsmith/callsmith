using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Callsmith.Desktop.Controls;
using FluentAssertions;

namespace Callsmith.Desktop.Tests;

public sealed class EnvVarCompletionUiTests
{
    [AvaloniaFact]
    public void TextBox_PopupOpensAndCommitsSelectedSuggestionViaKeyboard()
    {
        var textBox = new TextBox
        {
            Width = 320,
        };
        EnvVarCompletion.SetSuggestions(textBox,
        [
            new EnvVarSuggestion("base-url", "https://api.example.com"),
            new EnvVarSuggestion("bearer-token", "•••••"),
        ]);

        var window = CreateWindow(textBox);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        textBox.Focus();
        window.KeyTextInput("{{b");

        var popup = FindPopupList(window).Should().NotBeNull().Subject!;
        popup.ItemCount.Should().Be(2);
        popup.SelectedIndex.Should().Be(0);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        popup.SelectedIndex.Should().Be(1);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        textBox.Text.Should().Be("{{bearer-token}}");
        FindPopupList(window).Should().BeNull();
    }

    [AvaloniaFact]
    public void SyntaxEditor_PopupOpensAndCommitsSuggestionViaKeyboard()
    {
        var editor = new SyntaxEditor
        {
            Width = 320,
            Height = 120,
        };
        EnvVarCompletion.SetSuggestions(editor,
        [
            new EnvVarSuggestion("base-url", "https://api.example.com"),
            new EnvVarSuggestion("token", "•••••"),
        ]);

        var window = CreateWindow(editor);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        editor.Focus();
        window.KeyTextInput("{{ba");

        var popupBorder = FindPopupBorder(window).Should().NotBeNull().Subject!;
        var popup = popupBorder.Child.Should().BeOfType<ListBox>().Subject;
        popup.ItemCount.Should().Be(1);
        popup.SelectedItem.Should().BeOfType<EnvVarSuggestion>()
            .Which.Name.Should().Be("base-url");

        var overlay = window.GetVisualDescendants().OfType<OverlayLayer>().Single();
        var editorBottom = editor.TranslatePoint(new Point(0, editor.Bounds.Height), overlay).Should().NotBeNull().Subject;
        Canvas.GetTop(popupBorder).Should().BeLessThan(editorBottom.Y,
            "the SyntaxEditor completion popup should be anchored near the caret instead of beneath the entire control");

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        editor.Text.Should().Be("{{base-url}}");
        FindPopupList(window).Should().BeNull();
    }

    private static Window CreateWindow(Control content) => new()
    {
        Width = 500,
        Height = 300,
        Content = content,
    };

    private static ListBox? FindPopupList(Window window) =>
        FindPopupBorder(window)?.Child as ListBox;

    private static Border? FindPopupBorder(Window window) =>
        window.GetVisualDescendants()
            .OfType<OverlayLayer>()
            .SelectMany(layer => layer.Children.OfType<Border>())
            .Where(border => border.IsVisible)
            .SingleOrDefault();
}