using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Callsmith.Desktop.Controls;

public sealed partial class FilteredSyntaxViewer : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<FilteredSyntaxViewer, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<string> LanguageProperty =
        AvaloniaProperty.Register<FilteredSyntaxViewer, string>(nameof(Language), string.Empty);

    private TextBox? _filterTextBox;
    private Button? _clearButton;
    private TextBlock? _errorTextBlock;
    private Border? _filterTextBoxBorder;
    private Border? _filterBar;
    private SyntaxEditor? _editor;

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty || change.Property == LanguageProperty)
        {
            UpdateFilterAvailability();
            ApplyFilter();
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void AttachControls()
    {
        _filterTextBox = this.FindControl<TextBox>(nameof(FilterTextBox));
        _clearButton = this.FindControl<Button>(nameof(ClearButton));
        _errorTextBlock = this.FindControl<TextBlock>(nameof(ErrorTextBlock));
        _filterTextBoxBorder = this.FindControl<Border>(nameof(FilterTextBoxBorder));
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

        ApplyFilter();
    }

    private void UpdateFilterAvailability()
    {
        var enabled = SupportsFiltering();

        if (_filterBar is not null)
            _filterBar.IsVisible = enabled;

        if (_filterTextBox is not null)
        {
            _filterTextBox.Watermark = NormalizeLanguage() switch
            {
                "json" => "JSONPath filter (ex. $.library.books[*].author)",
                "xml" => "XPath filter (ex. /library/books/book/author)",
                _ => "Path",
            };
        }

        if (!enabled)
        {
            ClearError();
            SetFilteredState(isActive: false);
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
            SetFilteredState(isActive: false);
            _editor.Text = Text;
            return;
        }

        var expression = _filterTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expression))
        {
            ClearError();
            SetFilteredState(isActive: false);
            _editor.Text = Text;
            return;
        }

        if (SyntaxPathFilter.TryTransform(Text, Language, expression, out var transformed, out var error))
        {
            ClearError();
            SetFilteredState(isActive: true);
            _editor.Text = transformed;
            return;
        }

        ShowError(error);
        SetFilteredState(isActive: false);
        _editor.Text = Text;
    }

    private bool SupportsFiltering() => NormalizeLanguage() is "json" or "xml";

    private string NormalizeLanguage() => Language?.Trim().ToLowerInvariant() ?? string.Empty;

    private void ShowError(string error)
    {
        if (_errorTextBlock is null)
            return;

        _errorTextBlock.Text = error;
        _errorTextBlock.IsVisible = !string.IsNullOrWhiteSpace(error);
    }

    private void ClearError()
    {
        if (_errorTextBlock is null)
            return;

        _errorTextBlock.Text = string.Empty;
        _errorTextBlock.IsVisible = false;
    }

    private void SetFilteredState(bool isActive)
    {
        if (_filterTextBoxBorder is null)
            return;

        _filterTextBoxBorder.BorderBrush = isActive
            ? new SolidColorBrush(Color.Parse("#ce9178"))
            : new SolidColorBrush(Colors.Transparent);
    }
}
