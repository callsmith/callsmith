using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Callsmith.Desktop.Controls;

/// <summary>An environment variable suggestion carrying name and display value.</summary>
public sealed record EnvVarSuggestion(string Name, string Value);

// ─────────────────────────────────────────────────────────────────────────────
// EnvVarCompletion — attached property
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Attached property that adds {{…}} autocomplete to any TextBox.
/// When the user types {{ a filtered list of environment-variable names pops up.
/// Selecting an entry inserts the completed {{name}} token at the caret.
/// </summary>
public static class EnvVarCompletion
{
    // Avalonia requires a non-static owner type for attached-property registration.
    private sealed class Owner { }

    // One handler per TextBox; kept alive while the TextBox is alive.
    private static readonly ConditionalWeakTable<TextBox, CompletionHandler> Handlers = new();

    /// <summary>
    /// List of variable names to offer as completions.
    /// Bind to the active environment variable-name list.
    /// An empty or null list disables the feature.
    /// </summary>
    public static readonly AttachedProperty<IReadOnlyList<EnvVarSuggestion>?> SuggestionsProperty =
        AvaloniaProperty.RegisterAttached<Owner, TextBox, IReadOnlyList<EnvVarSuggestion>?>("Suggestions");

    static EnvVarCompletion()
    {
        SuggestionsProperty.Changed.AddClassHandler<TextBox>(OnSuggestionsChanged);
    }

    public static IReadOnlyList<EnvVarSuggestion>? GetSuggestions(TextBox element)
        => element.GetValue(SuggestionsProperty);

    public static void SetSuggestions(TextBox element, IReadOnlyList<EnvVarSuggestion>? value)
        => element.SetValue(SuggestionsProperty, value);

    private static void OnSuggestionsChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
    {
        var handler = Handlers.GetValue(textBox, static tb => new CompletionHandler(tb));
        handler.UpdateSuggestions((e.NewValue as IReadOnlyList<EnvVarSuggestion>) ?? []);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompletionHandler — per-TextBox overlay controller
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Manages the autocomplete overlay lifecycle for a single TextBox.
/// Uses OverlayLayer to render the panel so it floats above all other content
/// without requiring a Popup logical-parent relationship.
/// </summary>
internal sealed class CompletionHandler
{
    private readonly TextBox _textBox;

    private IReadOnlyList<EnvVarSuggestion> _suggestions = [];
    private List<EnvVarSuggestion> _currentItems = [];

    private Border? _overlayPanel;
    private ListBox? _listBox;
    private bool _isVisible;

    // Prevents re-entrant text-change processing during a commit.
    private bool _suppressChange;

    // TopLevel pointer handler for dismiss-on-click-outside.
    private TopLevel? _topLevel;
    private EventHandler<PointerPressedEventArgs>? _dismissHandler;

    public CompletionHandler(TextBox textBox)
    {
        _textBox = textBox;
        _textBox.TextChanged += OnTextChanged;
        _textBox.KeyDown += OnKeyDown;
        _textBox.AttachedToVisualTree += OnAttachedToVisualTree;
        _textBox.DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    // ─── Visual-tree lifecycle ────────────────────────────────────────────

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel = TopLevel.GetTopLevel(_textBox);
        if (_topLevel is null) return;

        _dismissHandler = OnTopLevelPointerPressed;
        _topLevel.AddHandler(
            InputElement.PointerPressedEvent,
            _dismissHandler,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ClosePanel();

        if (_overlayPanel != null)
        {
            var overlay = OverlayLayer.GetOverlayLayer(_textBox);
            overlay?.Children.Remove(_overlayPanel);
        }

        if (_topLevel is not null && _dismissHandler is not null)
        {
            _topLevel.RemoveHandler(InputElement.PointerPressedEvent, _dismissHandler);
            _topLevel = null;
            _dismissHandler = null;
        }
    }

    // ─── Dismiss on click outside ─────────────────────────────────────────

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isVisible) return;

        if (_overlayPanel is not null && e.Source is Visual src)
        {
            if (_overlayPanel == src || _overlayPanel.IsVisualAncestorOf(src))
                return;
        }

        ClosePanel();
    }

    // ─── Public API ───────────────────────────────────────────────────────

    public void UpdateSuggestions(IReadOnlyList<EnvVarSuggestion> suggestions)
    {
        _suggestions = suggestions;
        if (_isVisible)
            OnTextChanged(null, null!);
    }

    // ─── Text / keyboard handlers ─────────────────────────────────────────

    private void OnTextChanged(object? sender, TextChangedEventArgs? _)
    {
        if (_suppressChange) return;

        var trigger = FindTrigger();
        if (trigger is null || _suggestions.Count == 0)
        {
            ClosePanel();
            return;
        }

        var filtered = _suggestions
            .Where(s => s.Name.StartsWith(trigger.Prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (filtered.Count == 0)
        {
            ClosePanel();
            return;
        }

        OpenPanel(filtered);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isVisible) return;

        switch (e.Key)
        {
            case Key.Down:   MoveSelection(+1); e.Handled = true; break;
            case Key.Up:     MoveSelection(-1); e.Handled = true; break;
            case Key.Enter:  CommitSelection(); e.Handled = true; break;
            case Key.Tab:    CommitSelection(); break;
            case Key.Escape: ClosePanel();      e.Handled = true; break;
        }
    }

    // ─── Overlay open / close ─────────────────────────────────────────────

    private void OpenPanel(List<EnvVarSuggestion> items)
    {
        _currentItems = items;
        EnsurePanelCreated();

        _listBox!.ItemsSource = items;
        _listBox.SelectedIndex = 0;

        var overlay = OverlayLayer.GetOverlayLayer(_textBox);
        if (overlay is null) return;

        if (!overlay.Children.Contains(_overlayPanel!))
            overlay.Children.Add(_overlayPanel!);

        var pos = _textBox.TranslatePoint(new Point(0, _textBox.Bounds.Height), overlay);
        if (pos.HasValue)
        {
            Canvas.SetLeft(_overlayPanel!, pos.Value.X);
            Canvas.SetTop(_overlayPanel!, pos.Value.Y);
        }

        _overlayPanel!.Width = Math.Max(180, _textBox.Bounds.Width);
        _overlayPanel.IsVisible = true;
        _isVisible = true;
    }

    private void ClosePanel()
    {
        if (_overlayPanel is not null)
            _overlayPanel.IsVisible = false;
        _isVisible = false;
    }

    // ─── Commit ───────────────────────────────────────────────────────────

    private void CommitSelection()
    {
        if (_listBox?.SelectedItem is not EnvVarSuggestion chosen) return;

        var trigger = FindTrigger();
        if (trigger is null) { ClosePanel(); return; }

        var text = _textBox.Text ?? string.Empty;
        var token = $"{{{{{chosen.Name}}}}}";   // {{name}}
        var newText = string.Concat(
            text[..trigger.StartIndex],
            token,
            text[_textBox.CaretIndex..]);

        _suppressChange = true;
        try
        {
            _textBox.Text = newText;
            _textBox.CaretIndex = trigger.StartIndex + token.Length;
        }
        finally
        {
            _suppressChange = false;
        }

        ClosePanel();
        _textBox.Focus();
    }

    private void MoveSelection(int delta)
    {
        if (_listBox is null || _currentItems.Count == 0) return;
        var idx = Math.Clamp(_listBox.SelectedIndex + delta, 0, _currentItems.Count - 1);
        _listBox.SelectedIndex = idx;
        _listBox.ScrollIntoView(_currentItems[idx]);
    }

    // ─── Trigger detection ────────────────────────────────────────────────

    private TriggerContext? FindTrigger()
    {
        var text = _textBox.Text ?? string.Empty;
        var caret = _textBox.CaretIndex;
        if (caret == 0 || caret > text.Length) return null;

        var preceding = text[..caret];
        var idx = preceding.LastIndexOf("{{", StringComparison.Ordinal);
        if (idx < 0) return null;

        var after = preceding[(idx + 2)..];
        if (after.Contains("}}")) return null;

        return new TriggerContext(idx, after);
    }

    // ─── Panel construction ───────────────────────────────────────────────

    private void EnsurePanelCreated()
    {
        if (_overlayPanel is not null) return;

        _listBox = new ListBox
        {
            Background = new SolidColorBrush(Color.Parse("#252526")),
            Foreground = new SolidColorBrush(Color.Parse("#d4d4d4")),
            MaxHeight = 200,
            Padding = new Thickness(2),
        };

        _listBox.ItemTemplate = new FuncDataTemplate(
            typeof(EnvVarSuggestion),
            (item, _) =>
            {
                var s = (EnvVarSuggestion)item!;
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = "{{" + s.Name + "}}",
                    Padding = new Thickness(8, 4, 8, 1),
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#4ec9b0")),
                });
                panel.Children.Add(new TextBlock
                {
                    Text = s.Value.Length > 40 ? s.Value[..40] + "\u2026" : s.Value,
                    Padding = new Thickness(8, 1, 8, 4),
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#808080")),
                });
                return panel;
            },
            supportsRecycling: false);

        _listBox.Tapped += (_, _) => CommitSelection();

        _overlayPanel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#252526")),
            BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            BoxShadow = BoxShadows.Parse("0 4 14 2 #AA000000"),
            IsVisible = false,
            ZIndex = 9999,
            Child = _listBox,
        };
    }

    private sealed record TriggerContext(int StartIndex, string Prefix);
}
