using System.Collections.ObjectModel;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// A single editable row in the environment variable list.
/// Carries its own delete command so the view needs no parent binding.
/// Secret variables mask their value in the UI until revealed by toggling
/// <see cref="IsValueRevealed"/>.
/// When <see cref="HasSegments"/> is true, the value is composed from static text
/// and dynamic request references displayed as interactive pills.
/// </summary>
public sealed partial class EnvironmentVariableItemViewModel : ObservableObject
{
    private readonly Action<EnvironmentVariableItemViewModel> _onDelete;
    private readonly Action _onChanged;
    private readonly Func<IReadOnlyDictionary<string, string>> _getVariables;
    private readonly Func<DynamicValueSegment?, Task<DynamicValueSegment?>> _editDynamicSegment;
    private readonly Func<MockDataSegment?, Task<MockDataSegment?>> _editMockData;

    // Raw segments list (null = pure static variable)
    private List<ValueSegment>? _segments;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(PreviewValue))]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isSecret;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>
    /// When <c>true</c> the plain-text value input is displayed even for secret variables.
    /// When <c>false</c> and <see cref="IsSecret"/> is <c>true</c>, a password input is shown.
    /// </summary>
    [ObservableProperty]
    private bool _isValueRevealed;

    /// <summary>
    /// Free-form text that the user is typing after the last segment in the pill view.
    /// Flushed as a <see cref="StaticValueSegment"/> whenever a new dynamic segment is added.
    /// </summary>
    [ObservableProperty]
    private string _trailingText = string.Empty;

    /// <summary>
    /// Pill display items shown when <see cref="HasSegments"/> is true.
    /// Each item is either a static text label or a clickable dynamic-reference pill.
    /// </summary>
    public ObservableCollection<SegmentDisplayItem> DisplaySegments { get; } = [];

    /// <summary>True when this variable has dynamic segments (shows the pill view instead of a TextBox).</summary>
    public bool HasSegments => _segments is { Count: > 0 };

    /// <summary>True when the plain TextBox value input should be shown (no segments).</summary>
    public bool ShowPlainValueInput => !HasSegments;

    /// <summary>
    /// True when the trailing free-form input should be visible after the last pill.
    /// Shown only when the last segment is dynamic or mock; hidden when the last segment is static
    /// (the user can simply type into that last static TextBox instead).
    /// </summary>
    public bool HasTrailingInput => HasSegments && _segments?.LastOrDefault() is DynamicValueSegment or MockDataSegment;

    /// <summary>Removes this row from its parent list when executed.</summary>
    public IRelayCommand DeleteCommand { get; }

    public EnvironmentVariableItemViewModel(
        Action<EnvironmentVariableItemViewModel> onDelete,
        Action onChanged,
        Func<IReadOnlyDictionary<string, string>> getVariables,
        Func<DynamicValueSegment?, Task<DynamicValueSegment?>>? editDynamicSegment = null,
        Func<MockDataSegment?, Task<MockDataSegment?>>? editMockData = null)
    {
        ArgumentNullException.ThrowIfNull(onDelete);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(getVariables);
        _onDelete = onDelete;
        _onChanged = onChanged;
        _getVariables = getVariables;
        _editDynamicSegment = editDynamicSegment ?? (_ => Task.FromResult<DynamicValueSegment?>(null));
        _editMockData = editMockData ?? (_ => Task.FromResult<MockDataSegment?>(null));
        DeleteCommand = new RelayCommand(() => _onDelete(this));
    }

    // ─── Segment management ───────────────────────────────────────────────────

    /// <summary>
    /// Loads the variable's segments (if any) and rebuilds <see cref="DisplaySegments"/>.
    /// Called when the variable is first loaded from disk.
    /// </summary>
    public void LoadSegments(IReadOnlyList<ValueSegment>? segments)
    {
        _segments = segments is { Count: > 0 } ? [.. segments] : null;
        RebuildDisplaySegments();
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(ShowPlainValueInput));
    }

    /// <summary>
    /// Gets the effective segment list for persistence.
    /// Includes a trailing <see cref="StaticValueSegment"/> for any uncommitted
    /// <see cref="TrailingText"/> that the user has typed after the last pill.
    /// </summary>
    public IReadOnlyList<ValueSegment>? GetSegments()
    {
        if (_segments is not { Count: > 0 }) return null;
        if (string.IsNullOrEmpty(TrailingText)) return _segments.AsReadOnly();
        var result = new List<ValueSegment>(_segments) { new StaticValueSegment { Text = TrailingText } };
        return result.AsReadOnly();
    }

    /// <summary>
    /// Opens the dynamic value config dialog to add a new dynamic reference to the current value.
    /// When confirmed, the existing plain text is converted to a static segment and the new
    /// dynamic segment is appended.
    /// </summary>
    [RelayCommand]
    private async Task AddDynamicValueAsync()
    {
        var result = await _editDynamicSegment(null).ConfigureAwait(true);
        if (result is null) return;

        _segments ??= [];
        // Convert any existing plain text value to a leading static segment
        if (!string.IsNullOrEmpty(Value) && !HasSegments)
            _segments.Insert(0, new StaticValueSegment { Text = Value });
        // Flush any text typed in the trailing input as a static segment
        if (!string.IsNullOrEmpty(TrailingText))
        {
            _segments.Add(new StaticValueSegment { Text = TrailingText });
            TrailingText = string.Empty;
        }
        _segments.Add(result);
        Value = string.Empty;

        RebuildDisplaySegments();
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(ShowPlainValueInput));
        _onChanged();
    }

    /// <summary>
    /// Opens the mock data picker dialog to add a new mock data generator to the current value.
    /// </summary>
    [RelayCommand]
    private async Task AddMockDataAsync()
    {
        var result = await _editMockData(null).ConfigureAwait(true);
        if (result is null) return;

        _segments ??= [];
        if (!string.IsNullOrEmpty(Value) && !HasSegments)
            _segments.Insert(0, new StaticValueSegment { Text = Value });
        if (!string.IsNullOrEmpty(TrailingText))
        {
            _segments.Add(new StaticValueSegment { Text = TrailingText });
            TrailingText = string.Empty;
        }
        _segments.Add(result);
        Value = string.Empty;

        RebuildDisplaySegments();
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(ShowPlainValueInput));
        _onChanged();
    }

    /// <summary>
    /// Opens the mock data picker dialog to edit an existing mock data segment (triggered by clicking a pill).
    /// </summary>
    [RelayCommand]
    private async Task EditMockSegmentAsync(MockDataSegment segment)
    {
        var result = await _editMockData(segment).ConfigureAwait(true);
        if (result is null) return;

        if (_segments is null) return;
        var idx = _segments.IndexOf(segment);
        if (idx < 0) return;

        if (segment.Category == result.Category && segment.Field == result.Field)
            return;

        _segments[idx] = result;
        RebuildDisplaySegments();
        _onChanged();
    }

    /// <summary>
    /// Opens the config dialog to edit an existing dynamic segment (triggered by clicking a pill).
    /// </summary>
    [RelayCommand]
    private async Task EditDynamicSegmentAsync(DynamicValueSegment segment)
    {
        var result = await _editDynamicSegment(segment).ConfigureAwait(true);
        if (result is null) return;

        if (_segments is null) return;
        var idx = _segments.IndexOf(segment);
        if (idx < 0) return;

        // Only mark dirty if something actually changed — opening and previewing
        // an existing segment without modifying it should not dirty the environment.
        if (SegmentsEqual(segment, result))
            return;

        _segments[idx] = result;
        RebuildDisplaySegments();
        _onChanged();
    }

    /// <summary>Removes a dynamic segment from the segments list (the pill × button).</summary>
    [RelayCommand]
    private void RemoveDynamicSegment(ValueSegment segment)
    {
        _segments?.Remove(segment);
        if (_segments?.Count == 0)
        {
            _segments = null;
            // When exiting pill mode, recover any trailing text into the plain Value
            if (!string.IsNullOrEmpty(TrailingText))
            {
                Value = TrailingText;
                TrailingText = string.Empty;
            }
        }
        RebuildDisplaySegments();
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(ShowPlainValueInput));
        _onChanged();
    }

    // ─── Preview ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolved preview of <see cref="Value"/> with <c>{{VAR}}</c> tokens substituted.
    /// Returns <see langword="null"/> when the value contains no token references.
    /// </summary>
    public string? PreviewValue
    {
        get
        {
            if (!Value.Contains("{{")) return null;
            return VariableSubstitutionService.Substitute(Value, _getVariables());
        }
    }

    /// <summary>
    /// True when the value contains variable references and a preview is available to show.
    /// Secret values are excluded to avoid leaking resolved secrets in plain text.
    /// </summary>
    public bool HasPreview => !IsSecret && !HasSegments && Value.Contains("{{");

    /// <summary>Notifies bindings that <see cref="PreviewValue"/> and <see cref="HasPreview"/> may have changed.</summary>
    public void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewValue));
        OnPropertyChanged(nameof(HasPreview));
    }

    // ─── Property change callbacks ────────────────────────────────────────────

    partial void OnNameChanged(string value) => _onChanged();
    partial void OnValueChanged(string value) => _onChanged();
    partial void OnIsSecretChanged(bool value) => _onChanged();
    partial void OnIsEnabledChanged(bool value) => _onChanged();
    partial void OnTrailingTextChanged(string value) => _onChanged();

    // ─── Private helpers ─────────────────────────────────────────────────────

    private void RebuildDisplaySegments()
    {
        DisplaySegments.Clear();
        if (_segments is null) return;

        for (var i = 0; i < _segments.Count; i++)
        {
            var capturedIdx = i;
            var seg = _segments[i];
            if (seg is StaticValueSegment s)
            {
                DisplaySegments.Add(new SegmentDisplayItem(s.Text, newText =>
                {
                    if (_segments is null || capturedIdx >= _segments.Count) return;
                    if (string.IsNullOrEmpty(newText) && capturedIdx == _segments.Count - 1)
                    {
                        // Last static segment was cleared — remove it so the trailing ghost appears
                        _segments.RemoveAt(capturedIdx);
                        if (_segments.Count == 0) _segments = null;
                        RebuildDisplaySegments();
                        OnPropertyChanged(nameof(HasSegments));
                        OnPropertyChanged(nameof(ShowPlainValueInput));
                    }
                    else
                    {
                        _segments[capturedIdx] = new StaticValueSegment { Text = newText };
                    }
                    _onChanged();
                }, AddDynamicValueCommand, AddMockDataCommand));
            }
            else if (seg is MockDataSegment m)
            {
                DisplaySegments.Add(new SegmentDisplayItem(m, seg));
            }
            else if (seg is DynamicValueSegment d)
            {
                DisplaySegments.Add(new SegmentDisplayItem(d, seg));
            }
        }
        // Trailing input lives outside the ItemsControl in the view — no sentinel needed
        OnPropertyChanged(nameof(HasTrailingInput));
    }

    private static bool SegmentsEqual(DynamicValueSegment a, DynamicValueSegment b) =>
        a.RequestName == b.RequestName &&
        a.Path == b.Path &&
        a.Frequency == b.Frequency &&
        a.ExpiresAfterSeconds == b.ExpiresAfterSeconds;

    // ─── Nested types ────────────────────────────────────────────────────────

    /// <summary>
    /// A single display item in the segments pill view.
    /// Either an inline-editable static text field, a dynamic response-body reference pill,
    /// or a mock data generator pill.
    /// </summary>
    public sealed class SegmentDisplayItem : ObservableObject
    {
        private enum SegmentKind { Static, Dynamic, MockData }

        private readonly SegmentKind _kind;
        private readonly Action<string>? _onTextChanged;
        private string? _staticText;

        /// <summary>
        /// Text for static segments — two-way bindable so the user can edit it inline.
        /// Setter propagates changes back to the underlying <see cref="StaticValueSegment"/>.
        /// </summary>
        public string? StaticText
        {
            get => _staticText;
            set
            {
                if (SetProperty(ref _staticText, value))
                    _onTextChanged?.Invoke(value ?? string.Empty);
            }
        }

        /// <summary>Non-null for response-body dynamic segment pills.</summary>
        public DynamicValueSegment? DynamicSegment { get; }

        /// <summary>Non-null for mock data segment pills.</summary>
        public MockDataSegment? MockDataSegment { get; }

        /// <summary>The raw <see cref="ValueSegment"/> for removal operations.</summary>
        public ValueSegment? RawSegment { get; }

        /// <summary>
        /// Forwarded from the parent VM so the ContextMenu can bind to it directly
        /// (ContextMenu popups cannot traverse the visual tree via $parent).
        /// </summary>
        public IRelayCommand? AddDynamicValueCommand { get; }

        /// <summary>
        /// Forwarded from the parent VM so the ContextMenu can bind to it directly.
        /// </summary>
        public IRelayCommand? AddMockDataCommand { get; }

        public bool IsStatic => _kind == SegmentKind.Static;
        public bool IsDynamic => _kind == SegmentKind.Dynamic;
        public bool IsMockData => _kind == SegmentKind.MockData;

        /// <summary>Short display label for the pill.</summary>
        public string PillLabel => _kind switch
        {
            SegmentKind.Dynamic => DynamicSegment is null
                ? string.Empty
                : DynamicSegment.RequestName.Length > 25
                    ? "…" + DynamicSegment.RequestName[^22..]
                    : DynamicSegment.RequestName,
            SegmentKind.MockData => MockDataSegment is null
                ? string.Empty
                : $"{MockDataSegment.Category} · {MockDataSegment.Field}",
            _ => string.Empty,
        };

        /// <summary>Constructs a static text segment with inline-editing support.</summary>
        public SegmentDisplayItem(
            string staticText,
            Action<string> onTextChanged,
            IRelayCommand? addDynamicValueCommand = null,
            IRelayCommand? addMockDataCommand = null)
        {
            _kind = SegmentKind.Static;
            _staticText = staticText;
            _onTextChanged = onTextChanged;
            AddDynamicValueCommand = addDynamicValueCommand;
            AddMockDataCommand = addMockDataCommand;
        }

        /// <summary>Constructs a response-body dynamic reference segment pill.</summary>
        public SegmentDisplayItem(DynamicValueSegment dynamicSegment, ValueSegment rawSegment)
        {
            _kind = SegmentKind.Dynamic;
            DynamicSegment = dynamicSegment;
            RawSegment = rawSegment;
        }

        /// <summary>Constructs a mock data generator segment pill.</summary>
        public SegmentDisplayItem(MockDataSegment mockDataSegment, ValueSegment rawSegment)
        {
            _kind = SegmentKind.MockData;
            MockDataSegment = mockDataSegment;
            RawSegment = rawSegment;
        }
    }
}

