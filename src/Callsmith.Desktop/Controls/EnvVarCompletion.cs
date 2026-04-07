using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

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

    private static readonly ConditionalWeakTable<TextBox, TextBoxCompletionHandler> TextBoxHandlers = new();
    private static readonly ConditionalWeakTable<SyntaxEditor, SyntaxEditorCompletionHandler> SyntaxEditorHandlers = new();

    /// <summary>
    /// List of variable names to offer as completions.
    /// Bind to the active environment variable-name list.
    /// An empty or null list disables the feature.
    /// </summary>
    public static readonly AttachedProperty<IReadOnlyList<EnvVarSuggestion>?> SuggestionsProperty =
        AvaloniaProperty.RegisterAttached<Owner, InputElement, IReadOnlyList<EnvVarSuggestion>?>("Suggestions");

    static EnvVarCompletion()
    {
        SuggestionsProperty.Changed.AddClassHandler<TextBox>(OnTextBoxSuggestionsChanged);
        SuggestionsProperty.Changed.AddClassHandler<SyntaxEditor>(OnSyntaxEditorSuggestionsChanged);
    }

    public static IReadOnlyList<EnvVarSuggestion>? GetSuggestions(InputElement element)
        => element.GetValue(SuggestionsProperty);

    public static void SetSuggestions(InputElement element, IReadOnlyList<EnvVarSuggestion>? value)
        => element.SetValue(SuggestionsProperty, value);

    /// <summary>
    /// Returns the active <see cref="TextBoxCompletionHandler"/> for the given TextBox, if one
    /// has been created (i.e. suggestions were set on it). Intended for test use only.
    /// </summary>
    internal static TextBoxCompletionHandler? GetHandlerForTesting(TextBox textBox) =>
        TextBoxHandlers.TryGetValue(textBox, out var handler) ? handler : null;

    private static void OnTextBoxSuggestionsChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
    {
        var handler = TextBoxHandlers.GetValue(textBox, static tb => new TextBoxCompletionHandler(tb));
        handler.UpdateSuggestions((e.NewValue as IReadOnlyList<EnvVarSuggestion>) ?? []);
    }

    private static void OnSyntaxEditorSuggestionsChanged(SyntaxEditor editor, AvaloniaPropertyChangedEventArgs e)
    {
        var handler = SyntaxEditorHandlers.GetValue(editor, static value => new SyntaxEditorCompletionHandler(value));
        handler.UpdateSuggestions((e.NewValue as IReadOnlyList<EnvVarSuggestion>) ?? []);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompletionHandler — per-TextBox overlay controller
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Manages the autocomplete popup lifecycle for a single TextBox.
/// Uses a <see cref="Popup"/> so the dropdown renders in its own OS-level
/// overlay and is never clipped by a parent window or dialog.
/// </summary>
internal sealed class TextBoxCompletionHandler
{
    private readonly TextBox _textBox;

    private IReadOnlyList<EnvVarSuggestion> _suggestions = [];
    private List<EnvVarSuggestion> _currentItems = [];

    private Popup? _popup;
    private ListBox? _listBox;
    private bool _isVisible;

    // Prevents re-entrant text-change processing during a commit.
    private bool _suppressChange;

    public TextBoxCompletionHandler(TextBox textBox)
    {
        _textBox = textBox;
        _textBox.TextChanged += OnTextChanged;
        _textBox.KeyDown += OnKeyDown;
        _textBox.DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    // ─── Visual-tree lifecycle ────────────────────────────────────────────

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ClosePanel();
        _textBox.TextChanged -= OnTextChanged;
        _textBox.KeyDown -= OnKeyDown;
        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;
    }

    // ─── Public API ───────────────────────────────────────────────────────

    public void UpdateSuggestions(IReadOnlyList<EnvVarSuggestion> suggestions)
    {
        _suggestions = suggestions;
        if (_isVisible)
            OnTextChanged(null, null!);
    }

    // ─── Test helpers ─────────────────────────────────────────────────────

    /// <summary>Whether the completion popup is currently open. For test use only.</summary>
    internal bool IsPopupOpen => _popup?.IsOpen ?? false;

    /// <summary>The active list-box showing suggestions. For test use only.</summary>
    internal ListBox? CurrentListBox => _listBox;

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

        _popup!.MinWidth = Math.Max(180, _textBox.Bounds.Width);
        _popup.IsOpen = true;
        _isVisible = true;
    }

    private void ClosePanel()
    {
        if (_popup is not null)
            _popup.IsOpen = false;
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
        if (_popup is not null) return;

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

        var popupChild = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#252526")),
            BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            BoxShadow = BoxShadows.Parse("0 4 14 2 #AA000000"),
            Child = _listBox,
        };

        _popup = new Popup
        {
            PlacementTarget = _textBox,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = popupChild,
        };

        // Keep _isVisible in sync when the popup is dismissed externally (click outside).
        _popup.Closed += OnPopupClosed;
    }

    private void OnPopupClosed(object? sender, EventArgs e) => _isVisible = false;

    private sealed record TriggerContext(int StartIndex, string Prefix);
}

internal sealed class SyntaxEditorCompletionHandler
{
    private readonly SyntaxEditor _editor;

    private IReadOnlyList<EnvVarSuggestion> _suggestions = [];
    private List<EnvVarSuggestion> _currentItems = [];

    private Border? _overlayPanel;
    private ListBox? _listBox;
    private bool _isVisible;
    private bool _suppressChange;

    private TopLevel? _topLevel;
    private EventHandler<PointerPressedEventArgs>? _dismissHandler;

    public SyntaxEditorCompletionHandler(SyntaxEditor editor)
    {
        _editor = editor;
        _editor.TextChanged += OnTextChanged;
        _editor.KeyDown += OnKeyDown;
        _editor.AttachedToVisualTree += OnAttachedToVisualTree;
        _editor.DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public void UpdateSuggestions(IReadOnlyList<EnvVarSuggestion> suggestions)
    {
        _suggestions = suggestions;
        if (_isVisible)
            OnTextChanged(null, EventArgs.Empty);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel = TopLevel.GetTopLevel(_editor);
        if (_topLevel is null)
            return;

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
            var overlay = OverlayLayer.GetOverlayLayer(_editor);
            overlay?.Children.Remove(_overlayPanel);
        }

        if (_topLevel is not null && _dismissHandler is not null)
        {
            _topLevel.RemoveHandler(InputElement.PointerPressedEvent, _dismissHandler);
            _topLevel = null;
            _dismissHandler = null;
        }
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isVisible)
            return;

        if (_overlayPanel is not null && e.Source is Visual source)
        {
            if (_overlayPanel == source || _overlayPanel.IsVisualAncestorOf(source))
                return;
        }

        ClosePanel();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_suppressChange)
            return;

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
        if (!_isVisible)
            return;

        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                CommitSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePanel();
                e.Handled = true;
                break;
        }
    }

    private void OpenPanel(List<EnvVarSuggestion> items)
    {
        _currentItems = items;
        EnsurePanelCreated();

        _listBox!.ItemsSource = items;
        _listBox.SelectedIndex = 0;

        var overlay = OverlayLayer.GetOverlayLayer(_editor);
        if (overlay is null)
            return;

        if (!overlay.Children.Contains(_overlayPanel!))
            overlay.Children.Add(_overlayPanel!);

        var pos = GetCaretAnchorPoint(overlay) ?? _editor.TranslatePoint(new Point(0, _editor.Bounds.Height), overlay);
        if (pos.HasValue)
        {
            Canvas.SetLeft(_overlayPanel!, pos.Value.X);
            Canvas.SetTop(_overlayPanel!, pos.Value.Y);
        }

        _overlayPanel!.Width = Math.Max(240, _editor.Bounds.Width);
        _overlayPanel.IsVisible = true;
        _isVisible = true;
    }

    private void ClosePanel()
    {
        if (_overlayPanel is not null)
            _overlayPanel.IsVisible = false;
        _isVisible = false;
    }

    private void CommitSelection()
    {
        if (_listBox?.SelectedItem is not EnvVarSuggestion chosen)
            return;

        var trigger = FindTrigger();
        if (trigger is null)
        {
            ClosePanel();
            return;
        }

        var caret = GetCaretOffset();
        var token = $"{{{{{chosen.Name}}}}}";

        _suppressChange = true;
        try
        {
            if (_editor.Document is not null)
            {
                _editor.Document.Replace(trigger.StartIndex, caret - trigger.StartIndex, token);
                _editor.TextArea.Caret.Offset = trigger.StartIndex + token.Length;
            }
            else
            {
                var text = _editor.Text ?? string.Empty;
                _editor.Text = string.Concat(text[..trigger.StartIndex], token, text[caret..]);
                _editor.TextArea.Caret.Offset = trigger.StartIndex + token.Length;
            }
        }
        finally
        {
            _suppressChange = false;
        }

        ClosePanel();
        _editor.Focus();
    }

    private void MoveSelection(int delta)
    {
        if (_listBox is null || _currentItems.Count == 0)
            return;

        var idx = Math.Clamp(_listBox.SelectedIndex + delta, 0, _currentItems.Count - 1);
        _listBox.SelectedIndex = idx;
        _listBox.ScrollIntoView(_currentItems[idx]);
    }

    private TriggerContext? FindTrigger()
    {
        var text = _editor.Text ?? string.Empty;
        var caret = GetCaretOffset();
        if (caret == 0 || caret > text.Length)
            return null;

        var preceding = text[..caret];
        var idx = preceding.LastIndexOf("{{", StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var after = preceding[(idx + 2)..];
        if (after.Contains("}}", StringComparison.Ordinal))
            return null;

        return new TriggerContext(idx, after);
    }

    private int GetCaretOffset()
    {
        var offset = _editor.TextArea.Caret.Offset;
        return Math.Clamp(offset, 0, (_editor.Text ?? string.Empty).Length);
    }

    private Point? GetCaretAnchorPoint(Visual overlay)
    {
        var textView = _editor.TextArea.TextView;
        var document = _editor.Document;
        if (document is null)
            return null;

        try
        {
            textView.EnsureVisualLines();

            var offset = GetCaretOffset();
            var location = document.GetLocation(offset);
            var visualPosition = textView.GetVisualPosition(
                new TextViewPosition(location.Line, location.Column),
                VisualYPosition.LineBottom);

            return textView.TranslatePoint(visualPosition, overlay);
        }
        catch
        {
            return null;
        }
    }

    private void EnsurePanelCreated()
    {
        if (_overlayPanel is not null)
            return;

        _listBox = BuildListBox(CommitSelection);

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

    private static ListBox BuildListBox(Action onCommit)
    {
        var listBox = new ListBox
        {
            Background = new SolidColorBrush(Color.Parse("#252526")),
            Foreground = new SolidColorBrush(Color.Parse("#d4d4d4")),
            MaxHeight = 200,
            Padding = new Thickness(2),
        };

        listBox.ItemTemplate = new FuncDataTemplate(
            typeof(EnvVarSuggestion),
            (item, _) =>
            {
                var suggestion = (EnvVarSuggestion)item!;
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = "{{" + suggestion.Name + "}}",
                    Padding = new Thickness(8, 4, 8, 1),
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#4ec9b0")),
                });
                panel.Children.Add(new TextBlock
                {
                    Text = suggestion.Value.Length > 40 ? suggestion.Value[..40] + "\u2026" : suggestion.Value,
                    Padding = new Thickness(8, 1, 8, 4),
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#808080")),
                });
                return panel;
            },
            supportsRecycling: false);

        listBox.Tapped += (_, _) => onCommit();
        return listBox;
    }

    private sealed record TriggerContext(int StartIndex, string Prefix);
}
