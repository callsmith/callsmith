using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Drives the history panel: list of sent requests with filters,
/// and detail view of the selected entry.
/// </summary>
public sealed partial class HistoryPanelViewModel : ObservableObject
{
    private const string AllEnvironmentsOption = "All environments";
    private const string NoEnvironmentOption = "(no environment)";
    private readonly IHistoryService _historyService;
    private readonly EnvironmentViewModel? _environmentViewModel;
    private readonly IMessenger? _messenger;
    private readonly IAppPreferencesService? _appPreferencesService;
    private readonly CollectionsViewModel? _collectionsViewModel;
    private bool _preferencesLoaded;
    private int _nextPage = 0;
    private long _queryTotalCount = 0;
    private const int InitialChunkSize = 100;
    private const int IncrementalChunkSize = 100;
    private Guid? _scopedRequestId;
    private string? _scopedRequestName;
    private string? _pendingPurgeEnvironmentName;
    private int? _pendingPurgeDays;
    private bool _pendingPurgeAllTime;
    private bool _isRefreshingEnvironmentOptions;
    private CancellationTokenSource? _searchDebounceCts;
    private const int SearchDebounceMs = 400;

    // -------------------------------------------------------------------------
    // List state
    // -------------------------------------------------------------------------

    public ObservableCollection<HistoryEntryRowViewModel> Entries { get; } = [];

    [ObservableProperty]
    private HistoryEntryRowViewModel? _selectedEntry;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private long _totalCount;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isIncrementalLoading;

    [ObservableProperty]
    private string _historyListStatusMessage = string.Empty;

    [ObservableProperty]
    private string _resultCountLabel = string.Empty;

    // -------------------------------------------------------------------------
    // Filter state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private string _globalSearchText = string.Empty;

    public AdvancedHistorySearchViewModel AdvancedSearch { get; }

    [ObservableProperty]
    private bool _isAdvancedSearchOpen;

    public bool HasActiveAdvancedFilters => AdvancedSearch.ActiveFilterCount > 0;

    public int ActiveAdvancedFilterCount => AdvancedSearch.ActiveFilterCount;

    public ObservableCollection<HistoryEnvironmentOptionViewModel> EnvironmentOptions { get; } =
    [new HistoryEnvironmentOptionViewModel { Name = AllEnvironmentsOption, Color = null }];

    [ObservableProperty]
    private HistoryEnvironmentOptionViewModel? _selectedEnvironmentOption =
        new HistoryEnvironmentOptionViewModel { Name = AllEnvironmentsOption, Id = null, Color = null };

    // -------------------------------------------------------------------------
    // Detail state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private string _detailConfigured = string.Empty; 

    [ObservableProperty]
    private string _detailResolved = string.Empty;

    [ObservableProperty]
    private string _detailResponse = string.Empty;

    [ObservableProperty]
    private string _detailResponseLanguage = string.Empty;

    [ObservableProperty]
    private string _detailResponseHeaders = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailResponseHeaders))]
    private IReadOnlyList<ResponseHeaderRowViewModel> _detailResponseHeaderRows = [];

    [ObservableProperty]
    private string _detailSentAtDisplay = string.Empty;

    [ObservableProperty]
    private string _detailSentAtToolTip = string.Empty;

    [ObservableProperty]
    private bool _detailHasResponseBody;

    public bool HasDetailResponseHeaders => DetailResponseHeaderRows.Count > 0;

    [ObservableProperty]
    private bool _hasDetail;

    [ObservableProperty]
    private bool _isSecretShown;

    [ObservableProperty]
    private bool _isRevealingSecrets;

    [ObservableProperty]
    private bool _hasHiddenSecrets;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenRequestTooltip))]
    private bool _canOpenRequest;

    /// <summary>Whether the currently selected history entry has a file body available for download.</summary>
    [ObservableProperty]
    private bool _detailHasFileBody;

    /// <summary>Display name of the file body for the currently selected history entry.</summary>
    [ObservableProperty]
    private string _detailFileBodyName = string.Empty;

    /// <summary>Raw bytes of the currently displayed file body. Null when no file body is present.</summary>
    private byte[]? _detailFileBodyBytes;

    /// <summary>
    /// Tooltip text shown on the "Open Request" button when it is disabled.
    /// Returns <see langword="null"/> when the button is enabled (no tooltip needed).
    /// </summary>
    public string? OpenRequestTooltip => CanOpenRequest ? "Open this request in the request editor" : "Request not found";

    // -------------------------------------------------------------------------
    // Detail panel layout
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isHorizontalDetailLayout = true;

    [ObservableProperty]
    private bool _isClearHistoryConfirmOpen;

    [ObservableProperty]
    private bool _isPurgeDialogOpen;

    [ObservableProperty]
    private HistoryEnvironmentOptionViewModel? _selectedPurgeEnvironmentOption;

    [ObservableProperty]
    private string _purgeOlderThanDaysText = "90";

    [ObservableProperty]
    private bool _isPurgeAllTime;

    [ObservableProperty]
    private string? _purgeDialogErrorMessage;

    [ObservableProperty]
    private HistoryEntryRowViewModel? _pendingDeleteEntry;

    public string PendingDeleteEntryLabel
    {
        get
        {
            if (PendingDeleteEntry is null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(PendingDeleteEntry.Entry.RequestName))
                return PendingDeleteEntry.Entry.RequestName;

            if (!string.IsNullOrWhiteSpace(PendingDeleteEntry.Entry.ResolvedUrl))
                return PendingDeleteEntry.Entry.ResolvedUrl;

            return string.Empty;
        }
    }

    public string ClearHistoryConfirmTitle => "Delete records?";

    public string ClearHistoryConfirmDescription => "This cannot be undone. Continue?";

    public string ClearHistoryConfirmButtonLabel => "Delete";

    public string ClearHistoryConfirmEnvironmentLabel =>
        _pendingPurgeEnvironmentName ?? AllEnvironmentsOption;

    public string ClearHistoryConfirmDaysLabel =>
        (_pendingPurgeDays ?? 0).ToString(CultureInfo.InvariantCulture);

    public bool ClearHistoryConfirmIsAllTime => _pendingPurgeAllTime;

    public bool CanConfirmPurgeDialog =>
        SelectedPurgeEnvironmentOption is not null &&
        (IsPurgeAllTime || TryParsePurgeDays(PurgeOlderThanDaysText, out _));

    // -------------------------------------------------------------------------
    // Visibility
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isOpen;

    public bool HasMoreEntries => Entries.Count < _queryTotalCount;

    public bool IsRequestScoped => _scopedRequestId is not null;

    public string ScopedToLabel => IsRequestScoped ? $"Scoped to: {(_scopedRequestName ?? "(unnamed)")}" : string.Empty;

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            _ = ReloadEntriesAsync();
            if (!_preferencesLoaded && _appPreferencesService is not null)
                _ = LoadPreferencesAsync();
        }
    }

    private async Task LoadPreferencesAsync()
    {
        var prefs = await _appPreferencesService!.LoadAsync().ConfigureAwait(false);
        _preferencesLoaded = true;
        _historyDetailHorizontalSplitterFraction = prefs.HistoryDetailHorizontalSplitterFraction;
        _historyDetailVerticalSplitterFraction = prefs.HistoryDetailVerticalSplitterFraction;
        _historyListSplitterFraction = prefs.HistoryListSplitterFraction;
        IsHorizontalDetailLayout = prefs.IsHorizontalHistoryDetailLayout;
        // Always notify so the view applies persisted fractions even when the
        // layout flag didn't change from its default value.
        OnPropertyChanged(nameof(IsHorizontalDetailLayout));
        OnPropertyChanged(nameof(HistoryListSplitterFraction));
    }

    /// <summary>
    /// Fraction (0.0–1.0) of the available width occupied by the left panel in the
    /// history detail horizontal view. Null means the default 0.45 ratio is used.
    /// </summary>
    internal double? HistoryDetailHorizontalSplitterFraction => _historyDetailHorizontalSplitterFraction;

    /// <summary>
    /// Fraction (0.0–1.0) of the available height occupied by the top panel in the
    /// history detail vertical view. Null means the default 0.45 ratio is used.
    /// </summary>
    internal double? HistoryDetailVerticalSplitterFraction => _historyDetailVerticalSplitterFraction;

    /// <summary>
    /// Fraction (0.0–1.0) of the total width occupied by the history-list panel.
    /// Null means the default ratio is used.
    /// </summary>
    internal double? HistoryListSplitterFraction => _historyListSplitterFraction;

    private double? _historyDetailHorizontalSplitterFraction;
    private double? _historyDetailVerticalSplitterFraction;
    private double? _historyListSplitterFraction;

    /// <summary>
    /// Called by the view when the user finishes dragging the detail-panel splitter.
    /// Persists the new fraction so it can be restored on next launch.
    /// </summary>
    internal void OnDetailSplitterMoved(double fraction, bool isHorizontal)
    {
        if (isHorizontal)
            _historyDetailHorizontalSplitterFraction = fraction;
        else
            _historyDetailVerticalSplitterFraction = fraction;
        if (_appPreferencesService is not null)
        {
            var h = _historyDetailHorizontalSplitterFraction;
            var v = _historyDetailVerticalSplitterFraction;
            _ = _appPreferencesService.UpdateAsync(p => p with
            {
                HistoryDetailHorizontalSplitterFraction = h,
                HistoryDetailVerticalSplitterFraction = v
            });
        }
    }

    /// <summary>
    /// Called by the view when the user finishes dragging the history-list/detail splitter.
    /// Persists the new fraction so it can be restored on next launch.
    /// </summary>
    internal void OnListSplitterMoved(double fraction)
    {
        _historyListSplitterFraction = fraction;
        if (_appPreferencesService is not null)
            _ = _appPreferencesService.UpdateAsync(p => p with { HistoryListSplitterFraction = fraction });
    }

    partial void OnPendingDeleteEntryChanged(HistoryEntryRowViewModel? value)
        => OnPropertyChanged(nameof(PendingDeleteEntryLabel));

    partial void OnIsClearHistoryConfirmOpenChanged(bool value)
    {
        if (!value)
            ClearPendingPurgeSelection();
    }

    partial void OnIsPurgeDialogOpenChanged(bool value)
    {
        if (value)
            PurgeDialogErrorMessage = null;
    }

    partial void OnSelectedPurgeEnvironmentOptionChanged(HistoryEnvironmentOptionViewModel? value)
        => OnPropertyChanged(nameof(CanConfirmPurgeDialog));

    partial void OnPurgeOlderThanDaysTextChanged(string value)
    {
        PurgeDialogErrorMessage = null;
        OnPropertyChanged(nameof(CanConfirmPurgeDialog));
    }

    partial void OnIsPurgeAllTimeChanged(bool value)
    {
        PurgeDialogErrorMessage = null;
        OnPropertyChanged(nameof(CanConfirmPurgeDialog));
    }

    partial void OnSelectedEntryChanged(HistoryEntryRowViewModel? value)
    {
        IsSecretShown = false;
        CanOpenRequest = ComputeCanOpenRequest(value);
        if (value is not null)
        {
            HasHiddenSecrets = HasMaskedSecrets(value.Entry);
            _ = LoadDetailAsync(value.Entry);
        }
        else
        {
            HasHiddenSecrets = false;
            HasDetail = false;
            DetailConfigured = string.Empty;
            DetailResolved = string.Empty;
            DetailResponse = string.Empty;
            DetailResponseLanguage = string.Empty;
            DetailResponseHeaders = string.Empty;
            DetailResponseHeaderRows = [];
            DetailSentAtDisplay = string.Empty;
            DetailSentAtToolTip = string.Empty;
            DetailHasResponseBody = false;
            DetailHasFileBody = false;
            DetailFileBodyName = string.Empty;
            _detailFileBodyBytes = null;
        }
    }

    partial void OnSelectedEnvironmentOptionChanged(HistoryEnvironmentOptionViewModel? value)
    {
        if (_isRefreshingEnvironmentOptions)
            return;

        _ = SearchAsync();
    }

    partial void OnGlobalSearchTextChanged(string value)
    {
        var previous = _searchDebounceCts;
        _searchDebounceCts = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        _ = DebounceSearchAsync(_searchDebounceCts.Token);
    }

    private async Task DebounceSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, ct);
            await SearchAsync();
        }
        catch (OperationCanceledException) { }
    }

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public HistoryPanelViewModel(
        IHistoryService historyService,
        EnvironmentViewModel? environmentViewModel = null,
        IMessenger? messenger = null,
        IAppPreferencesService? appPreferencesService = null,
        CollectionsViewModel? collectionsViewModel = null)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        _historyService = historyService;
        _environmentViewModel = environmentViewModel;
        _messenger = messenger;
        _appPreferencesService = appPreferencesService;
        _collectionsViewModel = collectionsViewModel;

        AdvancedSearch = new AdvancedHistorySearchViewModel();
        AdvancedSearch.Applied += (_, _) =>
        {
            IsAdvancedSearchOpen = false;
            OnPropertyChanged(nameof(HasActiveAdvancedFilters));
            OnPropertyChanged(nameof(ActiveAdvancedFilterCount));
            _ = SearchAsync();
        };
        AdvancedSearch.Cancelled += (_, _) => IsAdvancedSearchOpen = false;
        AdvancedSearch.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AdvancedHistorySearchViewModel.ActiveFilterCount))
            {
                OnPropertyChanged(nameof(HasActiveAdvancedFilters));
                OnPropertyChanged(nameof(ActiveAdvancedFilterCount));
            }
        };
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void ToggleDetailLayout()
    {
        IsHorizontalDetailLayout = !IsHorizontalDetailLayout;
        var newValue = IsHorizontalDetailLayout;
        if (_appPreferencesService is not null)
            _ = _appPreferencesService.UpdateAsync(p => p with { IsHorizontalHistoryDetailLayout = newValue });
    }

    [RelayCommand]
    private void ShowAllHistory()
    {
        _scopedRequestId = null;
        _scopedRequestName = null;
        OnPropertyChanged(nameof(IsRequestScoped));
        OnPropertyChanged(nameof(ScopedToLabel));
        _nextPage = 0;
        _ = ReloadEntriesAsync();
    }

    [RelayCommand]
    private void ScopeToRequest(HistoryEntryRowViewModel? row)
    {
        if (IsRequestScoped || row?.Entry.RequestId is not { } requestId)
            return;

        OpenForRequest(requestId, row.Entry.RequestName);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchDebounceCts?.Cancel();
        _nextPage = 0;
        await ReloadEntriesAsync();
    }

    [RelayCommand]
    private void ClearGlobalSearchText() => GlobalSearchText = string.Empty;

    [RelayCommand]
    private void OpenAdvancedSearch() => IsAdvancedSearchOpen = true;

    [RelayCommand]
    private void ClearAdvancedFilters()
    {
        AdvancedSearch.ClearAllCommand.Execute(null);
        // Ensure badge updates even if the modal is not open.
        OnPropertyChanged(nameof(HasActiveAdvancedFilters));
        OnPropertyChanged(nameof(ActiveAdvancedFilterCount));
        _ = SearchAsync();
    }

    public async Task EnsureMoreEntriesAsync()
    {
        if (IsIncrementalLoading || !HasMoreEntries)
            return;

        IsIncrementalLoading = true;
        HistoryListStatusMessage = "Loading more records...";

        try
        {
            await LoadEntriesAsync(reset: false);
        }
        finally
        {
            IsIncrementalLoading = false;
            UpdateHistoryListStatusMessage();
        }
    }

    [RelayCommand]
    private async Task RevealSecretsAsync()
    {
        if (SelectedEntry is null || IsRevealingSecrets) return;
        IsRevealingSecrets = true;
        try
        {
            var revealed = await _historyService.RevealSensitiveFieldsAsync(
                SelectedEntry.Entry, CancellationToken.None);
            var values = await Task.Run(() => ComputeDetail(revealed, resolved: true));
            IsSecretShown = true;
            ApplyDetail(values);
        }
        catch
        {
            // ignore — button stays available
        }
        finally
        {
            IsRevealingSecrets = false;
        }
    }

    [RelayCommand]
    private async Task HideSecretsAsync()
    {
        if (SelectedEntry is null) return;
        IsSecretShown = false;
        await LoadDetailAsync(SelectedEntry.Entry);
    }

    [RelayCommand]
    private async Task ResendRequestAsync()
    {
        if (SelectedEntry is null || _messenger is null) return;

        // Always reveal secrets so resolved values include decrypted fields.
        var entry = await _historyService.RevealSensitiveFieldsAsync(
            SelectedEntry.Entry, CancellationToken.None);

        _messenger.Send(new ResendFromHistoryMessage(entry));
        IsOpen = false;
    }

    [RelayCommand]
    private void OpenRequest()
    {
        if (SelectedEntry?.Entry.RequestId is not { } requestId || _messenger is null) return;
        var request = _collectionsViewModel?.FindRequestByRequestId(requestId);
        if (request is null) return;
        _messenger.Send(new RequestSelectedMessage(request));
        IsOpen = false;
    }

    [RelayCommand]
    private void OpenRequestFromEntry(HistoryEntryRowViewModel? row)
    {
        if (row?.Entry.RequestId is not { } requestId || _messenger is null) return;
        var request = _collectionsViewModel?.FindRequestByRequestId(requestId);
        if (request is null) return;
        _messenger.Send(new RequestSelectedMessage(request));
        IsOpen = false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="row"/>'s request can be located
    /// in the currently loaded in-memory collection tree.
    /// </summary>
    public bool CanOpenEntryRequest(HistoryEntryRowViewModel? row) => ComputeCanOpenRequest(row);

    [RelayCommand]
    private void OpenPurgeDialog()
    {
        PurgeDialogErrorMessage = null;
        PurgeOlderThanDaysText = "90";
        IsPurgeAllTime = false;
        SelectedPurgeEnvironmentOption = ResolveEnvironmentOption(SelectedEnvironmentOption?.Id, SelectedEnvironmentOption?.Name)
            ?? EnvironmentOptions.FirstOrDefault();
        IsPurgeDialogOpen = true;
    }

    [RelayCommand]
    private void CancelPurgeDialog()
    {
        PurgeDialogErrorMessage = null;
        IsPurgeDialogOpen = false;
    }

    [RelayCommand]
    private void ConfirmPurgeDialog()
    {
        if (SelectedPurgeEnvironmentOption is null)
        {
            PurgeDialogErrorMessage = "Select an environment scope to continue.";
            return;
        }

        if (IsPurgeAllTime)
        {
            QueuePurgeConfirmation(SelectedPurgeEnvironmentOption, null, purgeAllTime: true);
            IsPurgeDialogOpen = false;
            return;
        }

        if (!TryParsePurgeDays(PurgeOlderThanDaysText, out var days))
        {
            PurgeDialogErrorMessage = "Enter a whole number of days greater than zero, or choose All time.";
            return;
        }

        QueuePurgeConfirmation(SelectedPurgeEnvironmentOption, days, purgeAllTime: false);
        IsPurgeDialogOpen = false;
    }

    [RelayCommand]
    private async Task ConfirmClearHistoryAsync()
    {
        var pendingEnvironmentName = _pendingPurgeEnvironmentName;
        var pendingDays = _pendingPurgeDays;
        var pendingPurgeAllTime = _pendingPurgeAllTime;
        IsClearHistoryConfirmOpen = false;

        if (pendingPurgeAllTime)
        {
            await _historyService.PurgeAllAsync(
                pendingEnvironmentName,
                _scopedRequestId,
                CancellationToken.None);
        }
        else
        {
            if (!pendingDays.HasValue || pendingDays.Value <= 0)
                return;

            var cutoff = DateTimeOffset.UtcNow.AddDays(-pendingDays.Value);
            await _historyService.PurgeOlderThanAsync(
                cutoff,
                pendingEnvironmentName,
                _scopedRequestId,
                CancellationToken.None);
        }

        _nextPage = 0;
        await ReloadEntriesAsync();
    }

    [RelayCommand]
    private void CancelClearHistory()
    {
        IsClearHistoryConfirmOpen = false;
    }

    [RelayCommand]
    private async Task RemoveEntryFromHistoryAsync(HistoryEntryRowViewModel? row)
    {
        if (row is null)
            return;

        await _historyService.DeleteByIdAsync(row.Entry.Id, CancellationToken.None);

        _nextPage = 0;
        await ReloadEntriesAsync();
    }

    [RelayCommand]
    private void RequestRemoveEntryFromHistory(HistoryEntryRowViewModel? row)
    {
        PendingDeleteEntry = row;
    }

    [RelayCommand]
    private async Task ConfirmRemoveEntryDeleteAsync()
    {
        if (PendingDeleteEntry is not { } row)
            return;

        PendingDeleteEntry = null;
        await RemoveEntryFromHistoryAsync(row);
    }

    [RelayCommand]
    private void CancelRemoveEntryDelete()
    {
        PendingDeleteEntry = null;
    }

    /// <summary>
    /// Callback injected by the view to show a platform save-file dialog.
    /// Receives the file bytes, the suggested file name, and a cancellation token.
    /// </summary>
    public Func<byte[], string, CancellationToken, Task>? SaveFileFunc { get; set; }

    [RelayCommand]
    private async Task DownloadFileBodyAsync(CancellationToken ct)
    {
        if (SaveFileFunc is null || _detailFileBodyBytes is null) return;
        var suggestedName = string.IsNullOrWhiteSpace(DetailFileBodyName)
            ? "file"
            : DetailFileBodyName;
        await SaveFileFunc(_detailFileBodyBytes, suggestedName, ct);
    }

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    private bool ComputeCanOpenRequest(HistoryEntryRowViewModel? row)
    {
        if (row?.Entry.RequestId is not { } requestId) return false;
        if (_collectionsViewModel is null) return false;
        return _collectionsViewModel.FindRequestByRequestId(requestId) is not null;
    }

    private async Task ReloadEntriesAsync()
    {
        Entries.Clear();
        SelectedEntry = null;
        _nextPage = 0;
        _queryTotalCount = 0;
        TotalCount = 0;
        ResultCountLabel = string.Empty;
        HistoryListStatusMessage = string.Empty;

        await LoadEntriesAsync(reset: true);
    }

    private async Task LoadEntriesAsync(bool reset)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var pageSize = reset ? InitialChunkSize : IncrementalChunkSize;
            var filter = BuildFilter(_nextPage, pageSize);

            var (entries, total) = await _historyService.QueryAsync(filter, CancellationToken.None);
            _queryTotalCount = total;
            TotalCount = total;

            foreach (var e in entries)
                Entries.Add(new HistoryEntryRowViewModel(e));

            if (entries.Count > 0)
                _nextPage++;

            if (reset)
                SelectedEntry = Entries.FirstOrDefault();

            RefreshEnvironmentOptionsFromShownEntries();

            ResultCountLabel = TotalCount > 0
                ? $"Showing {Entries.Count} of {TotalCount} results"
                : string.Empty;
            UpdateHistoryListStatusMessage();
            OnPropertyChanged(nameof(HasMoreEntries));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateHistoryListStatusMessage()
    {
        if (IsIncrementalLoading)
        {
            HistoryListStatusMessage = "Loading more records...";
            return;
        }

        if (!HasMoreEntries && Entries.Count > 0)
            HistoryListStatusMessage = "You've reached the end of history.";
        else
            HistoryListStatusMessage = string.Empty;
    }

    private void QueuePurgeConfirmation(
        HistoryEnvironmentOptionViewModel selectedScope,
        int? days,
        bool purgeAllTime)
    {
        _pendingPurgeEnvironmentName = string.Equals(selectedScope.Name, AllEnvironmentsOption, StringComparison.Ordinal)
            ? null
            : selectedScope.Name;
        _pendingPurgeDays = days;
        _pendingPurgeAllTime = purgeAllTime;
        OnPropertyChanged(nameof(ClearHistoryConfirmTitle));
        OnPropertyChanged(nameof(ClearHistoryConfirmDescription));
        OnPropertyChanged(nameof(ClearHistoryConfirmButtonLabel));
        OnPropertyChanged(nameof(ClearHistoryConfirmEnvironmentLabel));
        OnPropertyChanged(nameof(ClearHistoryConfirmDaysLabel));
        OnPropertyChanged(nameof(ClearHistoryConfirmIsAllTime));
        IsClearHistoryConfirmOpen = true;
    }

    private void ClearPendingPurgeSelection()
    {
        _pendingPurgeEnvironmentName = null;
        _pendingPurgeDays = null;
        _pendingPurgeAllTime = false;
        OnPropertyChanged(nameof(ClearHistoryConfirmEnvironmentLabel));
        OnPropertyChanged(nameof(ClearHistoryConfirmDaysLabel));
        OnPropertyChanged(nameof(ClearHistoryConfirmIsAllTime));
    }

    private static bool TryParsePurgeDays(string? text, out int days)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out days))
            return false;

        return days > 0;
    }

    private HistoryEnvironmentOptionViewModel? ResolveEnvironmentOption(Guid? id, string? name)
    {
        if (id.HasValue)
        {
            var byId = EnvironmentOptions.FirstOrDefault(option => option.Id == id.Value);
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(name))
            return EnvironmentOptions.FirstOrDefault();

        return EnvironmentOptions.FirstOrDefault(option =>
                   string.Equals(option.Name, name, StringComparison.Ordinal))
               ?? EnvironmentOptions.FirstOrDefault(option =>
                   string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private HistoryFilter BuildFilter(int page, int pageSize)
    {
        var adv = AdvancedSearch;
        var isAllEnvironments = string.Equals(SelectedEnvironmentOption?.Name, AllEnvironmentsOption, StringComparison.Ordinal);
        var isNoEnvironment = string.Equals(SelectedEnvironmentOption?.Name, NoEnvironmentOption, StringComparison.Ordinal)
            && SelectedEnvironmentOption?.Id is null;
        return new HistoryFilter
        {
            Page = page,
            PageSize = pageSize,
            NewestFirst = true,
            GlobalSearch = string.IsNullOrWhiteSpace(GlobalSearchText) ? null : GlobalSearchText.Trim(),
            NoEnvironment = isNoEnvironment,
            EnvironmentName = isAllEnvironments || isNoEnvironment
                ? null
                : SelectedEnvironmentOption?.Id is null
                    ? SelectedEnvironmentOption?.Name
                    : null,
            EnvironmentId = isAllEnvironments || isNoEnvironment
                ? null
                : SelectedEnvironmentOption?.Id,
            RequestContains = string.IsNullOrWhiteSpace(adv.RequestContains) ? null : adv.RequestContains.Trim(),
            ResponseContains = string.IsNullOrWhiteSpace(adv.ResponseContains) ? null : adv.ResponseContains.Trim(),
            Method = string.IsNullOrWhiteSpace(adv.MethodSearch) ? null : adv.MethodSearch.Trim(),
            MinStatusCode = adv.MinStatusCode,
            MaxStatusCode = adv.MaxStatusCode,
            SentAfter = adv.SentAfter,
            SentBefore = adv.SentBefore,
            MinElapsedMs = adv.MinElapsedMs,
            MaxElapsedMs = adv.MaxElapsedMs,
            RequestId = _scopedRequestId,
        };
    }

    public void OpenGlobal()
    {
        var wasOpen = IsOpen;
        _scopedRequestId = null;
        _scopedRequestName = null;
        OnPropertyChanged(nameof(IsRequestScoped));
        OnPropertyChanged(nameof(ScopedToLabel));
        IsOpen = true;
        if (wasOpen)
            _ = ReloadEntriesAsync();
    }

    public void OpenForRequest(Guid requestId, string? requestName)
    {
        var wasOpen = IsOpen;
        _scopedRequestId = requestId;
        _scopedRequestName = requestName;
        OnPropertyChanged(nameof(IsRequestScoped));
        OnPropertyChanged(nameof(ScopedToLabel));
        IsOpen = true;
        if (wasOpen)
            _ = ReloadEntriesAsync();
    }

    private void RefreshEnvironmentOptionsFromShownEntries()
    {
        var previousSelectionId = SelectedEnvironmentOption?.Id;
        var previousSelectionName = SelectedEnvironmentOption?.Name ?? AllEnvironmentsOption;
        var deletedOptions = BuildDeletedEnvironmentOptionsFromShownEntries();

        // Build the desired ordered list of options.
        var desired = new List<HistoryEnvironmentOptionViewModel>
        {
            new() { Name = AllEnvironmentsOption, Id = null, Color = null },
            new() { Name = NoEnvironmentOption, Id = null, Color = null },
        };

        if (_environmentViewModel is not null)
        {
            foreach (var env in _environmentViewModel.Environments)
            {
                if (!string.IsNullOrWhiteSpace(env.Name))
                    desired.Add(new HistoryEnvironmentOptionViewModel
                    {
                        Name = env.Name,
                        Id = env.EnvironmentId,
                        Color = env.Color,
                    });
            }
        }

        foreach (var option in deletedOptions.OrderBy(static o => o.Name, StringComparer.OrdinalIgnoreCase))
            desired.Add(new HistoryEnvironmentOptionViewModel
            {
                Name = option.Name,
                Id = option.Id,
                Color = option.Color,
            });

        // Sync EnvironmentOptions incrementally — no Clear() — so Avalonia's ComboBox
        // never receives a collection Reset and keeps its visual selection display.
        _isRefreshingEnvironmentOptions = true;
        try
        {
            // Remove items that are no longer needed.
            for (var i = EnvironmentOptions.Count - 1; i >= 0; i--)
            {
                if (!desired.Any(d => AreSameEnvironmentOption(d, EnvironmentOptions[i])))
                    EnvironmentOptions.RemoveAt(i);
            }

            // Insert new items and move existing ones into the desired order.
            for (var i = 0; i < desired.Count; i++)
            {
                var existingIdx = -1;
                for (var j = 0; j < EnvironmentOptions.Count; j++)
                {
                    if (AreSameEnvironmentOption(desired[i], EnvironmentOptions[j]))
                    {
                        existingIdx = j;
                        break;
                    }
                }

                if (existingIdx < 0)
                    EnvironmentOptions.Insert(Math.Min(i, EnvironmentOptions.Count), desired[i]);
                else if (existingIdx != i)
                    EnvironmentOptions.Move(existingIdx, i);
            }

            SelectedEnvironmentOption = ResolveEnvironmentOption(previousSelectionId, previousSelectionName)
                ?? EnvironmentOptions.FirstOrDefault();
        }
        finally
        {
            _isRefreshingEnvironmentOptions = false;
        }
    }

    private static bool AreSameEnvironmentOption(HistoryEnvironmentOptionViewModel a, HistoryEnvironmentOptionViewModel b)
    {
        // Match by stable ID when both have one, otherwise match by name.
        if (a.Id.HasValue && b.Id.HasValue)
            return a.Id == b.Id;
        return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds environment options for environments that appear in shown history entries
    /// but are no longer present in the current saved environments list.
    /// </summary>
    private IReadOnlyList<HistoryEnvironmentOption> BuildDeletedEnvironmentOptionsFromShownEntries()
    {
        HashSet<Guid>? currentById = null;
        HashSet<string>? currentByName = null;

        if (_environmentViewModel is not null)
        {
            currentById = new HashSet<Guid>(_environmentViewModel.Environments.Select(static e => e.EnvironmentId));
            currentByName = new HashSet<string>(
                _environmentViewModel.Environments
                    .Where(static e => !string.IsNullOrWhiteSpace(e.Name))
                    .Select(static e => e.Name!),
                StringComparer.OrdinalIgnoreCase);
        }

        var byKey = new Dictionary<string, (Guid? Id, string Name, string? Color, DateTimeOffset SentAt)>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Entries)
        {
            var entry = row.Entry;
            if (string.IsNullOrWhiteSpace(entry.EnvironmentName))
                continue;

            if (currentById is not null && currentByName is not null)
            {
                var isCurrent = entry.EnvironmentId.HasValue
                    ? currentById.Contains(entry.EnvironmentId.Value)
                    : currentByName.Contains(entry.EnvironmentName.Trim());

                if (isCurrent)
                    continue;
            }

            var key = entry.EnvironmentId.HasValue
                ? $"id:{entry.EnvironmentId.Value:D}"
                : $"name:{entry.EnvironmentName.Trim()}";

            if (!byKey.TryGetValue(key, out var existing) || entry.SentAt > existing.SentAt)
            {
                byKey[key] = (
                    entry.EnvironmentId,
                    entry.EnvironmentName,
                    string.IsNullOrWhiteSpace(entry.EnvironmentColor) ? null : entry.EnvironmentColor,
                    entry.SentAt);
            }
        }

        return byKey.Values
            .Select(static item => new HistoryEnvironmentOption
            {
                Id = item.Id,
                Name = item.Name,
                Color = item.Color,
            })
            .ToList();
    }

    private async Task LoadDetailAsync(HistoryEntry entry)
    {
        HasDetail = false;
        try
        {
            // Run all CPU-heavy work (string building, JSON/XML formatting) on a
            // background thread so the UI stays responsive while loading.
            var values = await Task.Run(() => ComputeDetail(entry, resolved: false));
            ApplyDetail(values);
            HasDetail = true;
        }
        catch
        {
            // non-critical
        }
    }

    /// <summary>
    /// Holds the precomputed string values and row models for the detail panel.
    /// Computed on a background thread in <see cref="ComputeDetail"/> and applied
    /// to observable properties on the UI thread in <see cref="ApplyDetail"/>.
    /// </summary>
    private sealed record DetailValues(
        string SentAtDisplay,
        string SentAtToolTip,
        string Configured,
        string Resolved,
        string ResponseLanguage,
        string Response,
        bool HasResponseBody,
        string ResponseHeaders,
        IReadOnlyList<ResponseHeaderRowViewModel> ResponseHeaderRows,
        bool HasFileBody,
        string FileBodyName,
        byte[]? FileBodyBytes);

    /// <summary>
    /// Applies a precomputed <see cref="DetailValues"/> to the observable properties.
    /// Language is set before text so AvaloniaEdit tokenises the document once with
    /// the correct highlighting instead of twice (once with stale highlighting when
    /// the text changes, then again when the language changes).
    /// Must be called on the UI thread.
    /// </summary>
    private void ApplyDetail(DetailValues values)
    {
        DetailSentAtDisplay = values.SentAtDisplay;
        DetailSentAtToolTip = values.SentAtToolTip;
        DetailConfigured = values.Configured;
        DetailResolved = values.Resolved;
        DetailResponseLanguage = values.ResponseLanguage;
        DetailResponse = values.Response;
        DetailHasResponseBody = values.HasResponseBody;
        DetailResponseHeaders = values.ResponseHeaders;
        DetailResponseHeaderRows = values.ResponseHeaderRows;
        DetailHasFileBody = values.HasFileBody;
        DetailFileBodyName = values.FileBodyName;
        _detailFileBodyBytes = values.FileBodyBytes;
    }

    /// <summary>
    /// Pure computation: builds all display strings and row models from a history entry.
    /// Safe to run on a background thread — no Avalonia or UI calls are made here.
    /// </summary>
    private static DetailValues ComputeDetail(HistoryEntry entry, bool resolved)
    {
        // Date/time of the entry
        var sentAtDisplay = entry.SentAt.LocalDateTime.ToString("G");
        var sentAtToolTip = entry.SentAt.LocalDateTime.ToString("F");

        // Configured tab — the raw template as the user wrote it
        var cfg = entry.ConfiguredSnapshot;
        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(entry.EnvironmentName)
            ? "(no environment)"
            : $"Environment: {entry.EnvironmentName}");
        sb.AppendLine($"{cfg.Method} {cfg.Url}");
        var enabledHeaders = cfg.Headers.Where(h => h.IsEnabled).ToList();
        if (enabledHeaders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Headers:");
            foreach (var h in enabledHeaders)
                sb.AppendLine($"  {h.Key}: {h.Value}");
        }
        if (cfg.PathParams.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Path Params:");
            foreach (var p in cfg.PathParams)
                sb.AppendLine($"  {p.Key}: {p.Value}");
        }
        var enabledQueryParams = cfg.QueryParams.Where(qp => qp.IsEnabled).ToList();
        if (enabledQueryParams.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Query Params:");
            foreach (var p in enabledQueryParams)
                sb.AppendLine($"  {p.Key}: {p.Value}");
        }
        if (cfg.Auth.AuthType != AuthConfig.AuthTypes.None)
        {
            sb.AppendLine();
            sb.AppendLine("Auth:");
            sb.AppendLine($"  Auth Type: {cfg.Auth.AuthType}");
            switch (cfg.Auth.AuthType)
            {
                case AuthConfig.AuthTypes.Basic:
                    sb.AppendLine($"  Username: {cfg.Auth.Username}");
                    break;
                case AuthConfig.AuthTypes.ApiKey:
                    sb.AppendLine($"  Key Name: {cfg.Auth.ApiKeyName}");
                    sb.AppendLine($"  Key In: {cfg.Auth.ApiKeyIn}");
                    break;
            }
        }
        if (!string.IsNullOrEmpty(cfg.Body))
        {
            sb.AppendLine();
            sb.AppendLine("Body:");
            sb.Append(cfg.Body);
        }
        if ((cfg.BodyType == CollectionRequest.BodyTypes.Form ||
             cfg.BodyType == CollectionRequest.BodyTypes.Multipart) && cfg.FormParams.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Body:");
            for (var i = 0; i < cfg.FormParams.Count; i++)
            {
                var p = cfg.FormParams[i];
                if (i < cfg.FormParams.Count - 1)
                    sb.AppendLine($"{p.Key}={p.Value}&");
                else
                    sb.Append($"{p.Key}={p.Value}");
            }
        }
        if (cfg.BodyType == CollectionRequest.BodyTypes.File)
        {
            sb.AppendLine();
            if (!string.IsNullOrEmpty(cfg.FileBodyName))
                sb.Append($"File: {cfg.FileBodyName}");
            else
                sb.Append("File: (binary data)");
        }

        var configured = TrimTrailingBlankLines(sb.ToString());

        // Resolved tab — rebuild wire-level view via HistorySentViewBuilder
        string resolvedText;
        try
        {
            var bindings = resolved
                ? entry.VariableBindings
                : entry.VariableBindings.Where(b => !b.IsSecret).ToList();
            var resolvedRequest = HistorySentViewBuilder.Build(cfg, bindings);
            var rb = new StringBuilder();
            var displayUrl = resolved 
                ? resolvedRequest.Url 
                : MaskAuthInUrl(resolvedRequest.Url, cfg.EffectiveAuth ?? cfg.Auth);
            rb.AppendLine($"{resolvedRequest.Method} {displayUrl}");
            if (resolvedRequest.Headers.Count > 0)
            {
                rb.AppendLine();
                rb.AppendLine("Headers:");
                foreach (var kv in resolvedRequest.Headers)
                {
                    var displayValue = resolved ? kv.Value : MaskAuthHeaderValue(kv.Key, kv.Value, cfg.EffectiveAuth ?? cfg.Auth);
                    rb.AppendLine($"  {kv.Key}: {displayValue}");
                }
            }
            if ((cfg.BodyType == CollectionRequest.BodyTypes.Form ||
                 cfg.BodyType == CollectionRequest.BodyTypes.Multipart) && cfg.FormParams.Count > 0)
            {
                var vars = HistorySentViewBuilder.BuildVariableMap(bindings);
                // Re-use BuildVariableMap on the secret-only subset so token normalisation
                // is consistent with the rest of the code and not duplicated here.
                var secretTokenNames = HistorySentViewBuilder
                    .BuildVariableMap(entry.VariableBindings.Where(b => b.IsSecret).ToList())
                    .Keys
                    .ToHashSet(StringComparer.Ordinal);
                rb.AppendLine();
                rb.AppendLine("Body:");
                var formParams = cfg.FormParams;
                for (var i = 0; i < formParams.Count; i++)
                {
                    var p = formParams[i];
                    var displayKey = FormatFormParamPart(p.Key, vars, secretTokenNames);
                    var displayValue = FormatFormParamPart(p.Value, vars, secretTokenNames);
                    if (i < formParams.Count - 1)
                        rb.AppendLine($"{displayKey}={displayValue}&");
                    else
                        rb.Append($"{displayKey}={displayValue}");
                }
            }
            else if (cfg.BodyType == CollectionRequest.BodyTypes.File)
            {
                rb.AppendLine();
                if (!string.IsNullOrEmpty(cfg.FileBodyName))
                    rb.Append($"File: {cfg.FileBodyName}");
                else
                    rb.Append("File: (binary data)");
            }
            else if (!string.IsNullOrEmpty(resolvedRequest.Body))
            {
                rb.AppendLine();
                rb.AppendLine("Body:");
                rb.Append(resolvedRequest.Body);
            }
            resolvedText = TrimTrailingBlankLines(rb.ToString());
        }
        catch
        {
            resolvedText = configured;
        }

        // Response tab
        var snap = entry.ResponseSnapshot;
        string responseLanguage;
        string responseBody;
        bool hasResponseBody;
        string responseHeaders;
        IReadOnlyList<ResponseHeaderRowViewModel> responseHeaderRows;
        if (snap is not null)
        {
            var contentType = TryGetContentType(snap.Headers);
            responseLanguage = GetResponseLanguage(contentType);
            responseBody = ResponseFormatter.FormatBody(snap.Body, contentType);
            hasResponseBody = !string.IsNullOrWhiteSpace(snap.Body);
            var rh = new StringBuilder();
            var rows = new List<ResponseHeaderRowViewModel>(snap.Headers.Count);
            var rowIndex = 0;
            foreach (var kv in snap.Headers)
            {
                rh.AppendLine($"{kv.Key}: {kv.Value}");
                rows.Add(new ResponseHeaderRowViewModel(kv.Key, kv.Value, rowIndex++));
            }
            responseHeaders = TrimTrailingBlankLines(rh.ToString());
            responseHeaderRows = rows;
        }
        else
        {
            responseLanguage = string.Empty;
            responseBody = "(no response recorded)";
            hasResponseBody = false;
            responseHeaders = string.Empty;
            responseHeaderRows = [];
        }

        return new DetailValues(
            SentAtDisplay: sentAtDisplay,
            SentAtToolTip: sentAtToolTip,
            Configured: configured,
            Resolved: resolvedText,
            ResponseLanguage: responseLanguage,
            Response: responseBody,
            HasResponseBody: hasResponseBody,
            ResponseHeaders: responseHeaders,
            ResponseHeaderRows: responseHeaderRows,
            HasFileBody: cfg.BodyType == CollectionRequest.BodyTypes.File && cfg.FileBodyBase64 is not null,
            FileBodyName: cfg.FileBodyName ?? string.Empty,
            FileBodyBytes: cfg.BodyType == CollectionRequest.BodyTypes.File && cfg.FileBodyBase64 is not null
                ? Convert.FromBase64String(cfg.FileBodyBase64)
                : null);
    }

    private static string? TryGetContentType(IReadOnlyDictionary<string, string> headers)
    {
        return headers.TryGetValue(WellKnownHeaders.ContentType, out var value)
            ? value
            : null;
    }

    private static string GetResponseLanguage(string? contentType)
    {
        var ct = contentType ?? string.Empty;
        if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)) return "json";
        if (ct.Contains("yaml", StringComparison.OrdinalIgnoreCase)) return "yaml";
        if (ct.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
            ct.Contains("xhtml", StringComparison.OrdinalIgnoreCase)) return "xml";
        if (ct.Contains("html", StringComparison.OrdinalIgnoreCase)) return "html";
        return string.Empty;
    }

    private static string TrimTrailingBlankLines(string value)
        => value.TrimEnd('\r', '\n');

    private static bool HasMaskedSecrets(HistoryEntry entry)
    {
        if (entry.VariableBindings.Any(b => b.IsSecret))
            return true;

        if (entry.ConfiguredSnapshot.Headers.FirstOrDefault(x => x.Key == WellKnownHeaders.Authorization) != null)
            return true;

        // Check the effective auth when available (covers inherited auth); fall back to raw auth.
        var auth = entry.ConfiguredSnapshot.EffectiveAuth ?? entry.ConfiguredSnapshot.Auth;
        return auth.AuthType switch
        {
            AuthConfig.AuthTypes.Bearer => !string.IsNullOrWhiteSpace(auth.Token),
            AuthConfig.AuthTypes.Basic => !string.IsNullOrWhiteSpace(auth.Username) || !string.IsNullOrWhiteSpace(auth.Password),
            AuthConfig.AuthTypes.ApiKey => !string.IsNullOrWhiteSpace(auth.ApiKeyName) && !string.IsNullOrWhiteSpace(auth.ApiKeyValue),
            _ => false,
        };
    }

    /// <summary>
    /// Masks auth-related header values to hide credentials (like the curl command builder does).
    /// Preserves Authorization header scheme but replaces token with &lt;token&gt;.
    /// Replaces API key values with &lt;key&gt;.
    /// </summary>
    private static string MaskAuthHeaderValue(string headerName, string headerValue, AuthConfig auth)
    {
        // Mask Authorization header — preserve scheme, replace value
        if (headerName.Equals(WellKnownHeaders.Authorization, StringComparison.OrdinalIgnoreCase))
        {
            var spaceIdx = headerValue.IndexOf(' ');
            return spaceIdx < 0 ? "<token>" : $"{headerValue[..spaceIdx]} <token>";
        }

        // Mask API key header if present
        if (!string.IsNullOrEmpty(auth.ApiKeyName) &&
            auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header &&
            headerName.Equals(auth.ApiKeyName, StringComparison.OrdinalIgnoreCase))
        {
            return "<key>";
        }

        return headerValue;
    }

    /// <summary>
    /// Masks API key query parameters in the URL to hide credentials.
    /// Replaces the value with &lt;key&gt;.
    /// </summary>
    private static string MaskAuthInUrl(string url, AuthConfig auth)
    {
        if (string.IsNullOrEmpty(auth.ApiKeyName) || 
            auth.ApiKeyIn != AuthConfig.ApiKeyLocations.Query)
        {
            return url;
        }

        // Use regex to find and mask the API key query parameter
        var paramName = Regex.Escape(auth.ApiKeyName);
        return Regex.Replace(
            url,
            $@"([?&]){paramName}=[^&]*",
            $"$1{auth.ApiKeyName}=<key>");
    }

    [GeneratedRegex(@"\{\{([^}]+)\}\}")]
    private static partial Regex FormTokenPattern();

    /// <summary>
    /// Substitutes <c>{{variable}}</c> tokens in a single form parameter key or value for display.
    /// Known non-secret tokens are replaced with their resolved value.
    /// Known secret tokens that were not resolved (because secrets are hidden) are shown as
    /// <c>&lt;secret&gt;</c>. Unrecognised tokens are left unchanged.
    /// </summary>
    private static string FormatFormParamPart(
        string template,
        IReadOnlyDictionary<string, string> vars,
        IReadOnlySet<string> secretTokenNames)
    {
        if (string.IsNullOrEmpty(template)) return template;

        return FormTokenPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            if (vars.TryGetValue(key, out var value))
                return value;
            if (secretTokenNames.Contains(key))
                return "<secret>";
            return match.Value;
        });
    }
}

