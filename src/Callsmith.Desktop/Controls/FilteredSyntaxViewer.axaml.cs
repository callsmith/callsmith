using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Services;

namespace Callsmith.Desktop.Controls;

public sealed partial class FilteredSyntaxViewer : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<FilteredSyntaxViewer, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<string> LanguageProperty =
        AvaloniaProperty.Register<FilteredSyntaxViewer, string>(nameof(Language), string.Empty);

    public static readonly StyledProperty<string> FilterExpressionProperty =
        AvaloniaProperty.Register<FilteredSyntaxViewer, string>(nameof(FilterExpression), string.Empty);

    private const string JsonFilterLabel = "JSONPATH FILTER:";
    private const string XmlFilterLabel = "XPATH FILTER:";

    private TextBox? _filterTextBox;
    private Button? _clearButton;
    private TextBlock? _filterLabelTextBlock;
    private TextBlock? _filterStatusTextBlock;
    private Border? _filterBar;
    private SyntaxEditor? _editor;

    /// <summary>
    /// Gets or sets the JSONPath query service used for JSON filtering.
    /// Defaults to a new <see cref="JsonPathService"/> instance. Can be replaced for testing or DI.
    /// </summary>
    public IJsonPathService JsonPath { get; set; } = new JsonPathService();

    public FilteredSyntaxViewer()
    {
        InitializeComponent();
        AttachControls();
        UpdateFilterAvailability();
        ApplyFilter();
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public string FilterExpression
    {
        get => GetValue(FilterExpressionProperty);
        set => SetValue(FilterExpressionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty || change.Property == LanguageProperty)
        {
            UpdateFilterAvailability();
            ApplyFilter();
        }

        if (change.Property == FilterExpressionProperty && _filterTextBox is not null)
        {
            var expression = change.GetNewValue<string>() ?? string.Empty;
            if (!string.Equals(_filterTextBox.Text, expression, StringComparison.Ordinal))
                _filterTextBox.Text = expression;
            ApplyFilter();
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void AttachControls()
    {
        _filterTextBox = this.FindControl<TextBox>(nameof(FilterTextBox));
        _clearButton = this.FindControl<Button>(nameof(ClearButton));
        _filterLabelTextBlock = this.FindControl<TextBlock>(nameof(FilterLabelTextBlock));
        _filterStatusTextBlock = this.FindControl<TextBlock>(nameof(FilterStatusTextBlock));
        _filterBar = this.FindControl<Border>(nameof(FilterBar));
        _editor = this.FindControl<SyntaxEditor>(nameof(Editor));

        if (_filterTextBox is not null)
            _filterTextBox.TextChanged += OnFilterTextChanged;

        if (_clearButton is not null)
            _clearButton.Click += (_, _) =>
            {
                if (_filterTextBox is not null)
                    _filterTextBox.Text = string.Empty;
            };
    }

    private void OnFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_clearButton is not null && _filterTextBox is not null)
            _clearButton.IsVisible = !string.IsNullOrWhiteSpace(_filterTextBox.Text);

        if (_filterTextBox is not null)
            FilterExpression = _filterTextBox.Text ?? string.Empty;

        ApplyFilter();
    }

    private void UpdateFilterAvailability()
    {
        var enabled = SupportsFiltering();

        if (_filterBar is not null)
            _filterBar.IsVisible = enabled;

        if (_filterLabelTextBlock is not null)
            _filterLabelTextBlock.Text = GetFilterLabel();

        if (_filterTextBox is not null)
        {
            _filterTextBox.Watermark = NormalizeLanguage() switch
            {
                "json" => "$.library.books[*].author",
                "xml" => "/library/books/book/author",
                _ => "Path",
            };
        }

        if (!enabled)
        {
            ClearError();
        }
    }

    private void ApplyFilter()
    {
        if (_editor is null)
            return;

        _editor.Language = Language;

        if (!SupportsFiltering())
        {
            ClearError();
            _editor.Text = Text;
            return;
        }

        var expression = _filterTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expression))
        {
            ClearError();
            _editor.Text = Text;
            return;
        }

        if (SyntaxPathFilter.TryTransform(Text, Language, expression, JsonPath, out var transformed, out var error))
        {
            ShowActive();
            _editor.Text = transformed;
            return;
        }

        ShowError(error);
        _editor.Text = Text;
    }

    private bool SupportsFiltering() => NormalizeLanguage() is "json" or "xml";

    private string NormalizeLanguage() => Language?.Trim().ToLowerInvariant() ?? string.Empty;

    private string GetFilterLabel() => NormalizeLanguage() switch
    {
        "json" => JsonFilterLabel,
        "xml" => XmlFilterLabel,
        _ => "FILTER:",
    };

    private void ShowActive()
    {
        if (_filterStatusTextBlock is null)
            return;

        _filterStatusTextBlock.Text = "Active";
        _filterStatusTextBlock.Foreground = new SolidColorBrush(Color.Parse("#6a9955"));
        _filterStatusTextBlock.FontWeight = FontWeight.Bold;
    }

    private void ShowError(string error)
    {
        if (_filterStatusTextBlock is null)
            return;

        _filterStatusTextBlock.Text = error;
        _filterStatusTextBlock.Foreground = new SolidColorBrush(Color.Parse("#f48771"));
        _filterStatusTextBlock.FontWeight = FontWeight.Normal;
    }

    private void ClearError()
    {
        if (_filterStatusTextBlock is null)
            return;

        _filterStatusTextBlock.Text = string.Empty;
        _filterStatusTextBlock.FontWeight = FontWeight.Normal;
    }
}
