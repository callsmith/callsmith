using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    private int _nextPage = 0;
    private long _queryTotalCount = 0;
    private const int InitialChunkSize = 100;
    private const int IncrementalChunkSize = 100;
    private Guid? _scopedRequestId;
    private string? _scopedRequestName;
    private string? _pendingPurgeEnvironmentName;
    private int? _pendingPurgeDays;
    private bool _pendingPurgeAllTime;

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
    private bool _hasDetail;

    [ObservableProperty]
    private bool _isSecretShown;

    [ObservableProperty]
    private bool _isRevealingSecrets;

    [ObservableProperty]
    private bool _hasHiddenSecrets;

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

    public string ScopeLabel => IsRequestScoped
        ? $"Scoped to request: {(_scopedRequestName ?? "(unnamed)")}" 
        : "Global history";

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
            _ = ReloadEntriesAsync();
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
        }
    }

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public HistoryPanelViewModel(
        IHistoryService historyService,
        EnvironmentViewModel? environmentViewModel = null)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        _historyService = historyService;
        _environmentViewModel = environmentViewModel;

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
    private void ShowAllHistory()
    {
        _scopedRequestId = null;
        _scopedRequestName = null;
        OnPropertyChanged(nameof(IsRequestScoped));
        OnPropertyChanged(nameof(ScopeLabel));
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
        // ClearAll fires Applied which already triggers a search and closes the modal;
        // also ensure badge updates even if the modal is not open.
        OnPropertyChanged(nameof(HasActiveAdvancedFilters));
        OnPropertyChanged(nameof(ActiveAdvancedFilterCount));
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
            IsSecretShown = true;
            PopulateDetail(revealed, resolved: true);
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

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

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
        OnPropertyChanged(nameof(ScopeLabel));
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
        OnPropertyChanged(nameof(ScopeLabel));
        IsOpen = true;
        if (wasOpen)
            _ = ReloadEntriesAsync();
    }

    private void RefreshEnvironmentOptionsFromShownEntries()
    {
        var previousSelectionId = SelectedEnvironmentOption?.Id;
        var previousSelectionName = SelectedEnvironmentOption?.Name ?? AllEnvironmentsOption;
        var deletedOptions = BuildDeletedEnvironmentOptionsFromShownEntries();

        EnvironmentOptions.Clear();
        EnvironmentOptions.Add(new HistoryEnvironmentOptionViewModel
        {
            Name = AllEnvironmentsOption,
            Id = null,
            Color = null,
        });
        EnvironmentOptions.Add(new HistoryEnvironmentOptionViewModel
        {
            Name = NoEnvironmentOption,
            Id = null,
            Color = null,
        });

        if (_environmentViewModel is not null)
        {
            foreach (var env in _environmentViewModel.Environments)
            {
                if (string.IsNullOrWhiteSpace(env.Name))
                    continue;

                EnvironmentOptions.Add(new HistoryEnvironmentOptionViewModel
                {
                    Name = env.Name,
                    Id = env.EnvironmentId,
                    Color = env.Color,
                });
            }
        }

        foreach (var option in deletedOptions.OrderBy(static o => o.Name, StringComparer.OrdinalIgnoreCase))
            EnvironmentOptions.Add(new HistoryEnvironmentOptionViewModel
            {
                Name = option.Name,
                Id = option.Id,
                Color = option.Color,
            });

        SelectedEnvironmentOption = ResolveEnvironmentOption(previousSelectionId, previousSelectionName)
            ?? EnvironmentOptions.FirstOrDefault();
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
            PopulateDetail(entry, resolved: false);
            HasDetail = true;
        }
        catch
        {
            // non-critical
        }
        await Task.CompletedTask;
    }

    private void PopulateDetail(HistoryEntry entry, bool resolved)
    {
        // Configured tab — the raw template as the user wrote it
        var cfg = entry.ConfiguredSnapshot;
        var sb = new StringBuilder();
        sb.AppendLine($"{cfg.Method} {cfg.Url}");
        if (!string.IsNullOrWhiteSpace(entry.EnvironmentName))
            sb.AppendLine($"Environment: {entry.EnvironmentName}");
        if (cfg.Headers.Any(h => h.IsEnabled))
        {
            sb.AppendLine();
            sb.AppendLine("Headers:");
            foreach (var h in cfg.Headers.Where(h => h.IsEnabled))
                sb.AppendLine($"  {h.Key}: {h.Value}");
        }
        if (cfg.AutoAppliedHeaders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Auto-applied:");
            foreach (var h in cfg.AutoAppliedHeaders)
                sb.AppendLine($"  {h.Key}: {h.Value}");
        }
        if (!string.IsNullOrEmpty(cfg.Body))
        {
            sb.AppendLine();
            sb.AppendLine("Body:");
            sb.Append(cfg.Body);
        }
        DetailConfigured = TrimTrailingBlankLines(sb.ToString());

        // Resolved tab — rebuild wire-level view via HistorySentViewBuilder
        try
        {
            var bindings = resolved
                ? entry.VariableBindings
                : entry.VariableBindings.Where(b => !b.IsSecret).ToList();
            var resolvedRequest = HistorySentViewBuilder.Build(cfg, bindings);
            var rb = new StringBuilder();
            var displayUrl = resolved 
                ? resolvedRequest.Url 
                : MaskAuthInUrl(resolvedRequest.Url, cfg.Auth);
            rb.AppendLine($"{resolvedRequest.Method} {displayUrl}");
            if (resolvedRequest.Headers.Count > 0)
            {
                rb.AppendLine();
                rb.AppendLine("Headers:");
                foreach (var kv in resolvedRequest.Headers)
                {
                    var displayValue = resolved ? kv.Value : MaskAuthHeaderValue(kv.Key, kv.Value, cfg.Auth);
                    rb.AppendLine($"  {kv.Key}: {displayValue}");
                }
            }
            if (!string.IsNullOrEmpty(resolvedRequest.Body))
            {
                rb.AppendLine();
                rb.AppendLine("Body:");
                rb.Append(resolvedRequest.Body);
            }
            if (!resolved && HasMaskedSecrets(entry))
                rb.AppendLine("\n[Secret values masked — click Reveal Secrets to show]");
            DetailResolved = TrimTrailingBlankLines(rb.ToString());
        }
        catch
        {
            DetailResolved = DetailConfigured;
        }

        // Response tab
        var snap = entry.ResponseSnapshot;
        if (snap is not null)
        {
            var contentType = TryGetContentType(snap.Headers);
            DetailResponse = ResponseFormatter.FormatBody(snap.Body, contentType);
            DetailResponseLanguage = GetResponseLanguage(contentType);
            var rh = new StringBuilder();
            foreach (var kv in snap.Headers)
                rh.AppendLine($"{kv.Key}: {kv.Value}");
            DetailResponseHeaders = TrimTrailingBlankLines(rh.ToString());
        }
        else
        {
            DetailResponse = "(no response recorded)";
            DetailResponseLanguage = string.Empty;
            DetailResponseHeaders = string.Empty;
        }
    }

    private static string? TryGetContentType(IReadOnlyDictionary<string, string> headers)
    {
        return headers.TryGetValue("Content-Type", out var value)
            ? value
            : null;
    }

    private static string GetResponseLanguage(string? contentType)
    {
        var ct = contentType ?? string.Empty;
        if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)) return "json";
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

        if (entry.ConfiguredSnapshot.Headers.FirstOrDefault(x => x.Key == "Authorization") != null)
            return true;

        var auth = entry.ConfiguredSnapshot.Auth;
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
        if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
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
        var paramName = System.Text.RegularExpressions.Regex.Escape(auth.ApiKeyName);
        return System.Text.RegularExpressions.Regex.Replace(
            url,
            $@"([?&]){paramName}=[^&]*",
            $"$1{auth.ApiKeyName}=<key>");
    }
}

