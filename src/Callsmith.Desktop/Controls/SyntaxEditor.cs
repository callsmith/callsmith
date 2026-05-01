using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Callsmith.Desktop.Controls;

/// <summary>
/// A syntax-highlighting code editor that extends AvaloniaEdit's <see cref="TextEditor"/>.
/// Adds a two-way bindable <see cref="Text"/> StyledProperty and a <see cref="Language"/>
/// property for runtime highlighting changes. Inheriting directly from TextEditor (rather
/// than wrapping it in a UserControl) ensures correct layout sizing inside grid rows.
/// </summary>
public sealed class SyntaxEditor : TextEditor
{
    private static readonly IHighlightingDefinition? JsonHighlighting;
    private static readonly IHighlightingDefinition? XmlHighlighting;
    private static readonly IHighlightingDefinition? HtmlHighlighting;
    private static readonly IHighlightingDefinition? TextHighlighting;
    private static readonly IHighlightingDefinition? YamlHighlighting;
    private readonly MenuItem _cutMenuItem;
    private readonly MenuItem _copyMenuItem;
    private readonly MenuItem _pasteMenuItem;
    private readonly MenuItem _selectAllMenuItem;
    private bool _updatingText;
    private bool _isInitialized;
    private FoldingManager? _foldingManager;
    private XmlFoldingStrategy? _xmlFoldingStrategy;
    private JsonFoldingStrategy? _jsonFoldingStrategy;
    private HtmlFoldingStrategy? _htmlFoldingStrategy;
    private YamlFoldingStrategy? _yamlFoldingStrategy;

    static SyntaxEditor()
    {
        JsonHighlighting = LoadXshd("avares://Callsmith/Highlighting/DarkJson.xshd");
        XmlHighlighting = LoadXshd("avares://Callsmith/Highlighting/DarkXml.xshd");
        HtmlHighlighting = LoadXshd("avares://Callsmith/Highlighting/DarkHtml.xshd");
        TextHighlighting = LoadXshd("avares://Callsmith/Highlighting/DarkText.xshd");
        YamlHighlighting = LoadXshd("avares://Callsmith/Highlighting/DarkYaml.xshd");
    }

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<SyntaxEditor, string>(nameof(Text), string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> LanguageProperty =
        AvaloniaProperty.Register<SyntaxEditor, string>(nameof(Language), string.Empty);

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

        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.AllowScrollBelowDocument = false;

        TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#264f78"));
        TextArea.CaretBrush = new SolidColorBrush(Color.Parse("#aeafad"));

        _cutMenuItem = new MenuItem { Header = "Cut" };
        _copyMenuItem = new MenuItem { Header = "Copy" };
        _pasteMenuItem = new MenuItem { Header = "Paste" };
        _selectAllMenuItem = new MenuItem { Header = "Select All" };

        _cutMenuItem.Click += (_, _) => Cut();
        _copyMenuItem.Click += (_, _) => Copy();
        _pasteMenuItem.Click += (_, _) => Paste();
        _selectAllMenuItem.Click += (_, _) => SelectAll();

        var contextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                _cutMenuItem,
                _copyMenuItem,
                _pasteMenuItem,
                new Separator(),
                _selectAllMenuItem,
            },
        };
        contextMenu.Opening += OnContextMenuOpening;
        ContextMenu = contextMenu;

        AddHandler(RequestBringIntoViewEvent, OnRequestBringIntoView, RoutingStrategies.Bubble);

        base.TextChanged += OnEditorTextChanged;

        _isInitialized = true;
        InstallFoldingManagerIfPossible();
        UpdateFoldings();

        // Disable the AvaloniaEdit internal undo stack so Ctrl+Z is handled exclusively
        // by the application-level UndoRedoService instead of the text editor.
        Document.UndoStack.SizeLimit = 0;
    }

    public new string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

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
            finally
            {
                _updatingText = false;
            }

            UpdateFoldings();
            return;
        }

        if (change.Property == DocumentProperty)
        {
            if (!_isInitialized)
                return;

            if (_foldingManager is not null)
                FoldingManager.Uninstall(_foldingManager);

            // Ensure the new document's undo stack is also disabled.
            Document.UndoStack.SizeLimit = 0;

            InstallFoldingManagerIfPossible();
            UpdateFoldings();
            return;
        }

        if (change.Property == LanguageProperty)
        {
            var lang = change.GetNewValue<string>();
            SyntaxHighlighting = ResolveHighlighting(lang);

            var normalizedLanguage = lang?.ToLowerInvariant();
            _xmlFoldingStrategy = normalizedLanguage == "xml" ? new XmlFoldingStrategy() : null;
            _jsonFoldingStrategy = normalizedLanguage == "json" ? new JsonFoldingStrategy() : null;
            _htmlFoldingStrategy = normalizedLanguage == "html" ? new HtmlFoldingStrategy() : null;
            _yamlFoldingStrategy = normalizedLanguage == "yaml" ? new YamlFoldingStrategy() : null;
            UpdateFoldings();
        }
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

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_updatingText)
            return;

        _updatingText = true;
        try
        {
            SetValue(TextProperty, base.Text);
        }
        finally
        {
            _updatingText = false;
        }

        UpdateFoldings();
    }

    private void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
        => e.Handled = true;

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hasSelection = !string.IsNullOrEmpty(SelectedText);
        var hasText = !string.IsNullOrEmpty(Text);
        var canEdit = !IsReadOnly;

        _cutMenuItem.IsEnabled = canEdit && hasSelection;
        _copyMenuItem.IsEnabled = hasSelection;
        _pasteMenuItem.IsEnabled = canEdit;
        _selectAllMenuItem.IsEnabled = hasText;
    }

    private void UpdateFoldings()
    {
        if (_foldingManager is null)
            return;

        try
        {
            if (_xmlFoldingStrategy is not null)
            {
                _xmlFoldingStrategy.UpdateFoldings(_foldingManager, Document);
                return;
            }

            if (_jsonFoldingStrategy is not null)
            {
                _jsonFoldingStrategy.UpdateFoldings(_foldingManager, Document);
                return;
            }

            if (_htmlFoldingStrategy is not null)
            {
                _htmlFoldingStrategy.UpdateFoldings(_foldingManager, Document);
                return;
            }

            if (_yamlFoldingStrategy is not null)
            {
                _yamlFoldingStrategy.UpdateFoldings(_foldingManager, Document);
                return;
            }

            _foldingManager.UpdateFoldings([], -1);
        }
        catch
        {
        }
    }

    private void InstallFoldingManagerIfPossible()
    {
        if (Document is null || TextArea.Document is null)
        {
            _foldingManager = null;
            return;
        }

        _foldingManager = FoldingManager.Install(TextArea);
    }

    private static IHighlightingDefinition? ResolveHighlighting(string? language) =>
        language?.ToLowerInvariant() switch
        {
            "json" => JsonHighlighting,
            "xml" => XmlHighlighting,
            "html" => HtmlHighlighting,
            "text" => TextHighlighting,
            "yaml" => YamlHighlighting,
            _ => null,
        };
}
