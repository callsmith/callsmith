using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using System.Xml;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// A syntax-highlighting code editor that extends AvaloniaEdit's <see cref="TextEditor"/>.
/// Adds a two-way bindable <see cref="Text"/> StyledProperty and a <see cref="Language"/>
/// property for runtime highlighting changes. Inheriting directly from TextEditor (rather
/// than wrapping it in a UserControl) ensures correct layout sizing inside grid rows.
/// </summary>
public sealed class SyntaxEditor : TextEditor
{
    private static readonly IHighlightingDefinition? _jsonHighlighting;
    private static readonly IHighlightingDefinition? _xmlHighlighting;
    private static readonly IHighlightingDefinition? _htmlHighlighting;
    private static readonly IHighlightingDefinition? _textHighlighting;
    private bool _updatingText;
    private FoldingManager? _foldingManager;
    private XmlFoldingStrategy? _xmlFoldingStrategy;

    static SyntaxEditor()
    {
        _jsonHighlighting = LoadXshd("avares://Callsmith/Highlighting/DarkJson.xshd");
        _xmlHighlighting  = LoadXshd("avares://Callsmith/Highlighting/DarkXml.xshd");
        _htmlHighlighting = LoadXshd("avares://Callsmith/Highlighting/DarkHtml.xshd");
        _textHighlighting = LoadXshd("avares://Callsmith/Highlighting/DarkText.xshd");
    }

    private static IHighlightingDefinition? LoadXshd(string uri)
    {
        try
        {
            var stream = Avalonia.Platform.AssetLoader.Open(new Uri(uri));
            using var reader = XmlReader.Create(stream);
            var xshd = HighlightingLoader.LoadXshd(reader);
            return HighlightingLoader.Load(xshd, HighlightingManager.Instance);
        }
        catch
        {
            return null;
        }
    }

    // ── Two-way bindable Text ──────────────────────────────────────────────

    /// <summary>Bindable text property. Bridges to/from the underlying TextEditor document.</summary>
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<SyntaxEditor, string>(nameof(Text), string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Gets or sets the editor content. Supports two-way Avalonia binding.</summary>
    public new string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // ── Style key: use TextEditor's ControlTheme, not a non-existent SyntaxEditor one ──
    protected override Type StyleKeyOverride => typeof(TextEditor);

    // ── Language ───────────────────────────────────────────────────────────

    /// <summary>
    /// Syntax language for highlighting. Accepted values: <c>"json"</c>, <c>"xml"</c>.
    /// Any other value shows plain text.
    /// </summary>
    public static readonly StyledProperty<string> LanguageProperty =
        AvaloniaProperty.Register<SyntaxEditor, string>(nameof(Language), string.Empty);

    public string Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    // ── Constructor ────────────────────────────────────────────────────────

    public SyntaxEditor()
    {
        FontFamily = new FontFamily("Consolas,Menlo,monospace");
        FontSize = 12;
        Background = new SolidColorBrush(Color.Parse("#1e1e1e"));
        Foreground = new SolidColorBrush(Color.Parse("#d4d4d4"));
        WordWrap = true;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ShowLineNumbers = true;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(8);

        // Don't turn URLs/emails into coloured hyperlinks — they should render as plain text.
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;

        TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#264f78"));
        TextArea.CaretBrush = new SolidColorBrush(Color.Parse("#aeafad"));

        _foldingManager = FoldingManager.Install(TextArea);

        // Propagate document text changes back to our StyledProperty.
        base.TextChanged += OnEditorTextChanged;
    }

    // ── Sync handlers ──────────────────────────────────────────────────────

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_updatingText) return;
        _updatingText = true;
        try { SetValue(TextProperty, base.Text); }
        finally { _updatingText = false; }
        UpdateFoldings();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !_updatingText)
        {
            _updatingText = true;
            try
            {
                var text = change.GetNewValue<string>() ?? string.Empty;
                if (base.Text != text)
                    base.Text = text;
            }
            finally { _updatingText = false; }
        }
        else if (change.Property == LanguageProperty)
        {
            var lang = change.GetNewValue<string>();
            SyntaxHighlighting = ResolveHighlighting(lang);
            _xmlFoldingStrategy = lang?.ToLowerInvariant() == "xml" ? new XmlFoldingStrategy() : null;
            UpdateFoldings();
        }
    }

    private void UpdateFoldings()
    {
        if (_foldingManager is null || _xmlFoldingStrategy is null) return;
        try { _xmlFoldingStrategy.UpdateFoldings(_foldingManager, Document); }
        catch { /* ignore malformed/incomplete document */ }
    }

    private static IHighlightingDefinition? ResolveHighlighting(string? language) =>
        language?.ToLowerInvariant() switch
        {
            "json" => _jsonHighlighting,
            "xml"  => _xmlHighlighting,
            "html" => _htmlHighlighting,
            "text" => _textHighlighting,
            _      => null,
        };

}

