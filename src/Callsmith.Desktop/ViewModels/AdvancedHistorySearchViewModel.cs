using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Holds the state and commands for the advanced history search modal.
/// The parent <see cref="HistoryPanelViewModel"/> owns an instance of this class and
/// reads its properties when building a <c>HistoryFilter</c>.
/// </summary>
public sealed partial class AdvancedHistorySearchViewModel : ObservableObject
{
    // -------------------------------------------------------------------------
    // Text filters
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    private string _requestContains = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    private string _responseContains = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    private string _methodSearch = string.Empty;

    // -------------------------------------------------------------------------
    // Status code range
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    private int? _minStatusCode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    private int? _maxStatusCode;

    // -------------------------------------------------------------------------
    // Date / time range
    // Avalonia DatePicker binds to DateTimeOffset? (date portion only).
    // Avalonia TimePicker binds to TimeSpan?.
    // We combine them into SentAfter / SentBefore when the filter is built.
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    [NotifyPropertyChangedFor(nameof(SentAfter))]
    private DateTimeOffset? _dateFromDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SentAfter))]
    private TimeSpan? _dateFromTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    [NotifyPropertyChangedFor(nameof(SentBefore))]
    private DateTimeOffset? _dateToDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SentBefore))]
    private TimeSpan? _dateToTime;

    /// <summary>
    /// The combined start of the date/time range, in local time.
    /// If <see cref="DateFromDate"/> is set but <see cref="DateFromTime"/> is null,
    /// the time defaults to midnight (start of day).
    /// </summary>
    public DateTimeOffset? SentAfter =>
        DateFromDate is { } d
            ? new DateTimeOffset(d.Date + (DateFromTime ?? TimeSpan.Zero), DateTimeOffset.Now.Offset)
            : null;

    /// <summary>
    /// The combined end of the date/time range, in local time.
    /// If <see cref="DateToDate"/> is set but <see cref="DateToTime"/> is null,
    /// the time defaults to 23:59:59 (end of day).
    /// </summary>
    public DateTimeOffset? SentBefore =>
        DateToDate is { } d
            ? new DateTimeOffset(d.Date + (DateToTime ?? new TimeSpan(23, 59, 59)), DateTimeOffset.Now.Offset)
            : null;

    // -------------------------------------------------------------------------
    // Elapsed time range (ms)
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    private long? _minElapsedMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount))]
    private long? _maxElapsedMs;

    // -------------------------------------------------------------------------
    // Derived
    // -------------------------------------------------------------------------

    /// <summary>Number of actively configured filter fields (for the badge in the parent view).</summary>
    public int ActiveFilterCount
    {
        get
        {
            var count = 0;
            if (!string.IsNullOrWhiteSpace(RequestContains)) count++;
            if (!string.IsNullOrWhiteSpace(ResponseContains)) count++;
            if (!string.IsNullOrWhiteSpace(MethodSearch)) count++;
            if (MinStatusCode.HasValue || MaxStatusCode.HasValue) count++;
            if (DateFromDate.HasValue || DateToDate.HasValue) count++;
            if (MinElapsedMs.HasValue || MaxElapsedMs.HasValue) count++;
            return count;
        }
    }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Raised when the user clicks Apply to execute the advanced search.</summary>
    public event EventHandler? Applied;

    /// <summary>Raised when the user clicks Cancel to discard changes and close the modal.</summary>
    public event EventHandler? Cancelled;

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void Apply() => Applied?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ClearRequestContains() => RequestContains = string.Empty;

    [RelayCommand]
    private void ClearResponseContains() => ResponseContains = string.Empty;

    [RelayCommand]
    private void ClearMethodSearch() => MethodSearch = string.Empty;

    [RelayCommand]
    private void SetDateFromToToday() => DateFromDate = DateTimeOffset.Now;

    [RelayCommand]
    private void SetDateToToToday() => DateToDate = DateTimeOffset.Now;

    [RelayCommand]
    private void SetDateFromTimeToNow() => DateFromTime = DateTimeOffset.Now.TimeOfDay;

    [RelayCommand]
    private void SetDateToTimeToNow() => DateToTime = DateTimeOffset.Now.TimeOfDay;

    [RelayCommand]
    private void ClearAll()
    {
        RequestContains = string.Empty;
        ResponseContains = string.Empty;
        MethodSearch = string.Empty;
        MinStatusCode = null;
        MaxStatusCode = null;
        DateFromDate = null;
        DateFromTime = null;
        DateToDate = null;
        DateToTime = null;
        MinElapsedMs = null;
        MaxElapsedMs = null;
    }
}
