using System.Collections.ObjectModel;
using Callsmith.Core.Helpers;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Reusable ViewModel for a single "value" field that supports:
/// <list type="bullet">
///   <item>Plain text input with <c>{{envVar}}</c> auto-complete</item>
///   <item>Pill rendering of <c>{% faker %}</c> and <c>{% response %}</c> inline tokens</item>
///   <item>Adding / editing / removing dynamic value segments via dialog callbacks</item>
/// </list>
/// Used by <see cref="EnvironmentVariableItemViewModel"/> (env variable rows) and
/// <see cref="KeyValueItemViewModel"/> (header, query param, auth, form-body rows).
/// </summary>
public sealed partial class SegmentedValueFieldViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private Func<DynamicValueSegment?, Task<DynamicValueSegment?>> _editDynamicSegment;
    private Func<MockDataSegment?, Task<MockDataSegment?>> _editMockData;

    // Raw segments (null = purely static / plain text)
    private List<ValueSegment>? _segments;

    // ── Observable state ──────────────────────────────────────────────────────

    /// <summary>The plain-text value (used when there are no segments, or as raw inline text).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(PreviewValue))]
    private string _text = string.Empty;

    /// <summary>Free-form text typed after the last pill in the segment view.</summary>
    [ObservableProperty]
    private string _trailingText = string.Empty;

    // ── Display items (pills + inline text boxes) ──────────────────────────────

    /// <summary>Pill + inline-text display items. Non-empty when <see cref="HasSegments"/>.</summary>
    public ObservableCollection<SegmentDisplayItem> DisplaySegments { get; } = [];

    /// <summary>True when the field has at least one dynamic/mock segment (pill view shown).</summary>
    public bool HasSegments => _segments is { Count: > 0 };

    /// <summary>True when the field should show the plain TextBox (no segments).</summary>
    public bool ShowPlainInput => !HasSegments;

    /// <summary>
    /// True when a trailing text-input should appear after the last pill
    /// (only visible when the last segment is dynamic or mock).
    /// </summary>
    public bool HasTrailingInput =>
        HasSegments && _segments?.LastOrDefault() is DynamicValueSegment or MockDataSegment;

    // ── Preview (env var substitution preview for static fields) ──────────────

    private Func<IReadOnlyDictionary<string, string>>? _getVariables;

    public string? PreviewValue
    {
        get
        {
            if (HasSegments || _getVariables is null) return null;
            if (!Text.Contains("{{")) return null;
            return VariableSubstitutionService.Substitute(Text, _getVariables());
        }
    }

    public bool HasPreview => !HasSegments && Text.Contains("{{");

    // ── Constructor ───────────────────────────────────────────────────────────

    public SegmentedValueFieldViewModel(
        Action onChanged,
        Func<DynamicValueSegment?, Task<DynamicValueSegment?>>? editDynamicSegment = null,
        Func<MockDataSegment?, Task<MockDataSegment?>>? editMockData = null,
        Func<IReadOnlyDictionary<string, string>>? getVariables = null)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _onChanged = onChanged;
        _editDynamicSegment =
            editDynamicSegment ?? (_ => Task.FromResult<DynamicValueSegment?>(null));
        _editMockData =
            editMockData ?? (_ => Task.FromResult<MockDataSegment?>(null));
        _getVariables = getVariables;
    }

    /// <summary>
    /// Updates the segment-editing dialog callbacks.
    /// Called when callbacks become available after construction (e.g. loaded rows
    /// that were created before the parent ViewModel had dialogs wired up).
    /// </summary>
    public void SetCallbacks(
        Func<DynamicValueSegment?, Task<DynamicValueSegment?>> editDynamicSegment,
        Func<MockDataSegment?, Task<MockDataSegment?>> editMockData)
    {
        _editDynamicSegment = editDynamicSegment;
        _editMockData = editMockData;
    }

    // ── Segment loading / persistence ────────────────────────────────────────

    /// <summary>
    /// Loads the value from raw inline text. Parses any <c>{% %}</c> tokens into segments.
    /// </summary>
    public void LoadFromText(string? value)
    {
        var segments = SegmentSerializer.ParseToSegments(value);
        _segments = segments is { Count: > 0 } ? [.. segments] : null;

        if (_segments is null)
        {
            Text = value ?? string.Empty;
        }
        else
        {
            // Restore plain text from the serialized segments for edit-mode back-compat
            Text = string.Empty;
        }

        RebuildDisplaySegments();
        NotifySegmentProperties();
    }

    /// <summary>
    /// Loads pre-parsed segments (from an <see cref="EnvironmentVariable.Segments"/> list).
    /// </summary>
    public void LoadFromSegments(IReadOnlyList<ValueSegment>? segments)
    {
        _segments = segments is { Count: > 0 } ? [.. segments] : null;
        RebuildDisplaySegments();
        NotifySegmentProperties();
    }

    /// <summary>
    /// Gets the effective inline-text representation for persistence.
    /// Includes any uncommitted <see cref="TrailingText"/>.
    /// </summary>
    public string GetInlineText()
    {
        if (!HasSegments) return Text;
        var segs = GetSegments();
        return segs is null ? Text : SegmentSerializer.SerializeSegments(segs);
    }

    /// <summary>Gets the current segment list (including trailing text if present).</summary>
    public IReadOnlyList<ValueSegment>? GetSegments()
    {
        if (_segments is not { Count: > 0 }) return null;
        if (!string.IsNullOrEmpty(TrailingText))
        {
            var result = new List<ValueSegment>(_segments) { new StaticValueSegment { Text = TrailingText } };
            return result.AsReadOnly();
        }
        return _segments.AsReadOnly();
    }

    // ── Segment mutation commands ─────────────────────────────────────────────

    [RelayCommand]
    private async Task AddDynamicValueAsync()
    {
        var result = await _editDynamicSegment(null).ConfigureAwait(true);
        if (result is null) return;

        _segments ??= [];
        FlushPlainText();
        FlushTrailingText();
        _segments.Add(result);
        Text = string.Empty;

        RebuildDisplaySegments();
        NotifySegmentProperties();
        _onChanged();
    }

    [RelayCommand]
    private async Task AddMockDataAsync()
    {
        var result = await _editMockData(null).ConfigureAwait(true);
        if (result is null) return;

        _segments ??= [];
        FlushPlainText();
        FlushTrailingText();
        _segments.Add(result);
        Text = string.Empty;

        RebuildDisplaySegments();
        NotifySegmentProperties();
        _onChanged();
    }

    [RelayCommand]
    private async Task EditMockSegmentAsync(MockDataSegment segment)
    {
        var result = await _editMockData(segment).ConfigureAwait(true);
        if (result is null || _segments is null) return;
        var idx = _segments.IndexOf(segment);
        if (idx < 0 || (segment.Category == result.Category && segment.Field == result.Field)) return;

        _segments[idx] = result;
        RebuildDisplaySegments();
        _onChanged();
    }

    [RelayCommand]
    private async Task EditDynamicSegmentAsync(DynamicValueSegment segment)
    {
        var result = await _editDynamicSegment(segment).ConfigureAwait(true);
        if (result is null || _segments is null) return;
        var idx = _segments.IndexOf(segment);
        if (idx < 0) return;
        if (SegmentsEqual(segment, result)) return;

        _segments[idx] = result;
        RebuildDisplaySegments();
        _onChanged();
    }

    [RelayCommand]
    private void RemoveSegment(ValueSegment segment)
    {
        _segments?.Remove(segment);
        if (_segments?.Count == 0)
        {
            _segments = null;
            if (!string.IsNullOrEmpty(TrailingText))
            {
                Text = TrailingText;
                TrailingText = string.Empty;
            }
        }
        RebuildDisplaySegments();
        NotifySegmentProperties();
        _onChanged();
    }

    // ── Property change notifications ────────────────────────────────────────

    partial void OnTextChanged(string value)        => _onChanged();
    partial void OnTrailingTextChanged(string value) => _onChanged();

    public void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewValue));
        OnPropertyChanged(nameof(HasPreview));
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void FlushPlainText()
    {
        if (!string.IsNullOrEmpty(Text) && !HasSegments)
            _segments!.Insert(0, new StaticValueSegment { Text = Text });
    }

    private void FlushTrailingText()
    {
        if (!string.IsNullOrEmpty(TrailingText))
        {
            _segments!.Add(new StaticValueSegment { Text = TrailingText });
            TrailingText = string.Empty;
        }
    }

    private void NotifySegmentProperties()
    {
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(ShowPlainInput));
        OnPropertyChanged(nameof(HasTrailingInput));
    }

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
                        _segments.RemoveAt(capturedIdx);
                        if (_segments.Count == 0) _segments = null;
                        RebuildDisplaySegments();
                        NotifySegmentProperties();
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
        OnPropertyChanged(nameof(HasTrailingInput));
    }

    private static bool SegmentsEqual(DynamicValueSegment a, DynamicValueSegment b) =>
        a.RequestName == b.RequestName &&
        a.Path == b.Path &&
        a.Frequency == b.Frequency &&
        a.ExpiresAfterSeconds == b.ExpiresAfterSeconds;

    // ── Nested display-item type (mirrors EnvironmentVariableItemViewModel.SegmentDisplayItem) ──

    /// <summary>
    /// A single display item in the pill view. Shared by all usages of
    /// <see cref="SegmentedValueFieldViewModel"/>.
    /// </summary>
    public sealed class SegmentDisplayItem : ObservableObject
    {
        private enum Kind { Static, Dynamic, Mock }

        private readonly Kind _kind;
        private readonly Action<string>? _onTextChanged;
        private string? _staticText;

        public string? StaticText
        {
            get => _staticText;
            set
            {
                if (SetProperty(ref _staticText, value))
                    _onTextChanged?.Invoke(value ?? string.Empty);
            }
        }

        public DynamicValueSegment? DynamicSegment { get; }
        public MockDataSegment?     MockDataSegment { get; }
        public ValueSegment?        RawSegment      { get; }

        public IRelayCommand? AddDynamicValueCommand { get; }
        public IRelayCommand? AddMockDataCommand     { get; }

        public bool IsStatic  => _kind == Kind.Static;
        public bool IsDynamic => _kind == Kind.Dynamic;
        public bool IsMockData => _kind == Kind.Mock;

        public string PillLabel => _kind switch
        {
            Kind.Dynamic => DynamicSegment is null ? string.Empty
                : DynamicSegment.RequestName.Length > 25
                    ? "…" + DynamicSegment.RequestName[^22..]
                    : DynamicSegment.RequestName,
            Kind.Mock => MockDataSegment is null ? string.Empty
                : $"{MockDataSegment.Category} · {MockDataSegment.Field}",
            _ => string.Empty,
        };

        public SegmentDisplayItem(
            string staticText,
            Action<string> onTextChanged,
            IRelayCommand? addDynamicValueCommand = null,
            IRelayCommand? addMockDataCommand = null)
        {
            _kind = Kind.Static;
            _staticText = staticText;
            _onTextChanged = onTextChanged;
            AddDynamicValueCommand = addDynamicValueCommand;
            AddMockDataCommand = addMockDataCommand;
        }

        public SegmentDisplayItem(DynamicValueSegment dynamicSegment, ValueSegment rawSegment)
        {
            _kind = Kind.Dynamic;
            DynamicSegment = dynamicSegment;
            RawSegment = rawSegment;
        }

        public SegmentDisplayItem(MockDataSegment mockDataSegment, ValueSegment rawSegment)
        {
            _kind = Kind.Mock;
            MockDataSegment = mockDataSegment;
            RawSegment = rawSegment;
        }
    }
}
