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
    private readonly IHistoryService _historyService;
    private readonly EnvironmentViewModel? _environmentViewModel;
    private int _currentPage;
    private const int PageSize = 100;
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
        new HistoryEnvironmentOptionViewModel { Name = AllEnvironmentsOption, Color = null };

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

    public bool IsRequestScoped => _scopedRequestId is not null;

    public string ScopeLabel => IsRequestScoped
        ? $"Scoped to request: {(_scopedRequestName ?? "(unnamed)")}" 
        : "Global history";

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
            _ = LoadPageAsync(reset: true);
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
        _currentPage = 0;
        _ = LoadPageAsync(reset: true);
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
        _currentPage = 0;
        await LoadPageAsync(reset: true);
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

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        _currentPage++;
        await LoadPageAsync(reset: false);
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
        SelectedPurgeEnvironmentOption = ResolveEnvironmentOption(SelectedEnvironmentOption?.Name)
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

        _currentPage = 0;
        await LoadPageAsync(reset: true);
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

        _currentPage = 0;
        await LoadPageAsync(reset: true);
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

    private async Task LoadPageAsync(bool reset)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;

        if (reset)
        {
            Entries.Clear();
            SelectedEntry = null;
        }

        try
        {
            await RefreshEnvironmentOptionsAsync();
            var filter = BuildFilter();
            var (entries, total) = await _historyService.QueryAsync(filter, CancellationToken.None);
            TotalCount = total;

            foreach (var e in entries)
                Entries.Add(new HistoryEntryRowViewModel(e));

            if (reset)
                SelectedEntry = Entries.FirstOrDefault();
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

    private HistoryEnvironmentOptionViewModel? ResolveEnvironmentOption(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return EnvironmentOptions.FirstOrDefault();

        return EnvironmentOptions.FirstOrDefault(option =>
                   string.Equals(option.Name, name, StringComparison.Ordinal))
               ?? EnvironmentOptions.FirstOrDefault(option =>
                   string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private HistoryFilter BuildFilter()
    {
        var adv = AdvancedSearch;
        return new HistoryFilter
        {
            Page = _currentPage,
            PageSize = PageSize,
            NewestFirst = true,
            GlobalSearch = string.IsNullOrWhiteSpace(GlobalSearchText) ? null : GlobalSearchText.Trim(),
            EnvironmentName = string.Equals(SelectedEnvironmentOption?.Name, AllEnvironmentsOption, StringComparison.Ordinal)
                ? null
                : SelectedEnvironmentOption?.Name,
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
        _scopedRequestId = null;
        _scopedRequestName = null;
        OnPropertyChanged(nameof(IsRequestScoped));
        OnPropertyChanged(nameof(ScopeLabel));
        IsOpen = true;
        _currentPage = 0;
        _ = LoadPageAsync(reset: true);
    }

    public void OpenForRequest(Guid requestId, string? requestName)
    {
        _scopedRequestId = requestId;
        _scopedRequestName = requestName;
        OnPropertyChanged(nameof(IsRequestScoped));
        OnPropertyChanged(nameof(ScopeLabel));
        IsOpen = true;
        _currentPage = 0;
        _ = LoadPageAsync(reset: true);
    }

    private async Task RefreshEnvironmentOptionsAsync()
    {
        var options = await _historyService.GetEnvironmentOptionsAsync(_scopedRequestId, CancellationToken.None);
        var previousSelection = SelectedEnvironmentOption?.Name ?? AllEnvironmentsOption;
        var orderedOptions = OrderEnvironmentOptions(options);

        EnvironmentOptions.Clear();
        EnvironmentOptions.Add(new HistoryEnvironmentOptionViewModel
        {
            Name = AllEnvironmentsOption,
            Color = null,
        });

        foreach (var option in orderedOptions)
            EnvironmentOptions.Add(new HistoryEnvironmentOptionViewModel
            {
                Name = option.Name,
                Color = ResolveEnvironmentSwatchColor(option.Name, option.Color),
            });

        SelectedEnvironmentOption = EnvironmentOptions
            .FirstOrDefault(o => string.Equals(o.Name, previousSelection, StringComparison.Ordinal))
            ?? EnvironmentOptions.FirstOrDefault();
    }

    private IReadOnlyList<HistoryEnvironmentOption> OrderEnvironmentOptions(
        IReadOnlyList<HistoryEnvironmentOption> options)
    {
        if (_environmentViewModel is null || options.Count == 0)
            return options;

        var currentOrderByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var env in _environmentViewModel.Environments)
        {
            if (string.IsNullOrWhiteSpace(env.Name) || currentOrderByName.ContainsKey(env.Name))
                continue;

            currentOrderByName.Add(env.Name, index);
            index++;
        }

        return options
            .OrderBy(option => currentOrderByName.ContainsKey(option.Name) ? 0 : 1)
            .ThenBy(option => currentOrderByName.TryGetValue(option.Name, out var currentIndex) ? currentIndex : int.MaxValue)
            .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string? ResolveEnvironmentSwatchColor(string environmentName, string? historicalColor)
    {
        // 1) If this environment currently exists, always use its current configured color.
        var currentMatch = _environmentViewModel?.Environments
            .FirstOrDefault(env => string.Equals(env.Name, environmentName, StringComparison.Ordinal));
        if (currentMatch is not null)
            return string.IsNullOrWhiteSpace(currentMatch.Color) ? null : currentMatch.Color;

        // 2) Otherwise fall back to the most recent historical color, if any.
        if (!string.IsNullOrWhiteSpace(historicalColor))
            return historicalColor;

        // 3) Nothing found.
        return null;
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
        if (cfg.Headers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Headers:");
            foreach (var h in cfg.Headers)
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
                rb.AppendLine("\n[Secret values masked — click Reveal to show]");
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

