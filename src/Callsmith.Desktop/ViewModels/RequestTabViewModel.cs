using System.Net.Http;
using System.Text;
using Avalonia.Threading;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using Callsmith.Desktop.Controls;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// State for a single open request tab. Each tab is an independent editor instance.
/// <see cref="RequestEditorViewModel"/> manages the collection of open tabs and
/// handles cross-tab concerns (environment changes, collection events).
/// </summary>
public sealed partial class RequestTabViewModel : ObservableObject
{
    private readonly ITransportRegistry _transportRegistry;
    private readonly ICollectionService _collectionService;
    private readonly IDynamicVariableEvaluator? _dynamicEvaluator;
    private readonly IMessenger _messenger;
    private readonly Action<RequestTabViewModel> _requestClose;
    private readonly IHistoryService? _historyService;

    /// <summary>Source request loaded from disk. Null for brand-new unsaved tabs.</summary>
    private CollectionRequest? _sourceRequest;

    private EnvironmentModel? _activeEnvironment;
    private EnvironmentModel _globalEnvironment = new() { FilePath = string.Empty, Name = "Global", Variables = [], EnvironmentId = Guid.NewGuid() };
    private bool _loading;
    private bool _saving;
    private bool _syncingUrl;
    private bool _syncingPathParams;
    private long _historyHydrationVersion;

    // -------------------------------------------------------------------------
    // Tab identity
    // -------------------------------------------------------------------------

    /// <summary>Stable identity across the tab's lifetime.</summary>
    public Guid TabId { get; } = Guid.NewGuid();

    /// <summary>True when this tab is the currently selected/active tab.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// True for tabs that have never been saved to disk.
    /// Cleared once the user completes Save As.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonLabel))]
    private bool _isNew;

    /// <summary>File path of the loaded request, or empty string if the tab is new.</summary>
    public string SourceFilePath => _sourceRequest?.FilePath ?? string.Empty;

    /// <summary>True when this tab is backed by a saved request with a stable RequestId.</summary>
    public bool CanOpenRequestHistory => _sourceRequest?.RequestId is not null;

    /// <summary>Text shown on the tab chip: request name or "New Request".</summary>
    public string TabTitle => string.IsNullOrWhiteSpace(RequestName) ? "New Request" : RequestName;

    /// <summary>
    /// True when the tab needs a save action: either has disk changes, or is a new unsaved tab.
    /// Drives the tab dirty-dot and the Save button's enabled state.
    /// </summary>
    public bool TabIsDirty => HasUnsavedChanges || IsNew;

    /// <summary>Label for the Save button: "Save" for existing requests, "Save As…" for new tabs.</summary>
    public string SaveButtonLabel => IsNew ? "Save As…" : "Save";

    // -------------------------------------------------------------------------
    // Request editor state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MethodColor))]
    [NotifyPropertyChangedFor(nameof(MethodPillLabel))]
    private string _selectedMethod = "GET";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewUrl))]
    [NotifyPropertyChangedFor(nameof(HasUnresolvedPathParams))]
    [NotifyPropertyChangedFor(nameof(PreviewUrlForeground))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private string _requestName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBodyEditor))]
    [NotifyPropertyChangedFor(nameof(ShowTextBodyEditor))]
    [NotifyPropertyChangedFor(nameof(ShowFormBodyEditor))]
    [NotifyPropertyChangedFor(nameof(IsBodyJson))]
    [NotifyPropertyChangedFor(nameof(BodyLanguage))]
    private string _selectedBodyType = CollectionRequest.BodyTypes.None;

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthBearer))]
    [NotifyPropertyChangedFor(nameof(IsAuthBasic))]
    [NotifyPropertyChangedFor(nameof(IsAuthApiKey))]
    private string _authType = AuthConfig.AuthTypes.None;

    [ObservableProperty] private string _authToken = string.Empty;
    [ObservableProperty] private string _authUsername = string.Empty;
    [ObservableProperty] private string _authPassword = string.Empty;
    [ObservableProperty] private bool _showAuthPassword = false;
    [ObservableProperty] private string _authApiKeyName = string.Empty;
    [ObservableProperty] private string _authApiKeyValue = string.Empty;
    [ObservableProperty] private string _authApiKeyIn = AuthConfig.ApiKeyLocations.Header;

    /// <summary>Segmented field for the bearer token (supports pill rendering of dynamic tokens).</summary>
    public SegmentedValueFieldViewModel AuthTokenField { get; }

    /// <summary>Segmented field for the basic auth username.</summary>
    public SegmentedValueFieldViewModel AuthUsernameField { get; }

    /// <summary>Segmented field for the basic auth password.</summary>
    public SegmentedValueFieldViewModel AuthPasswordField { get; }

    /// <summary>Segmented field for the API key name.</summary>
    public SegmentedValueFieldViewModel AuthApiKeyNameField { get; }

    /// <summary>Segmented field for the API key value (supports pill rendering of dynamic tokens).</summary>
    public SegmentedValueFieldViewModel AuthApiKeyValueField { get; }

    // -------------------------------------------------------------------------
    // Save state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabIsDirty))]
    private bool _hasUnsavedChanges;

    // -------------------------------------------------------------------------
    // Save As panel (new tabs only)
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private bool _showSaveAsPanel;

    [ObservableProperty]
    private string _saveAsName = "New Request";

    /// <summary>
    /// The folder path the user has selected in the Save As panel.
    /// Defaults to the collection root; the user can change it via the dropdown.
    /// </summary>
    [ObservableProperty]
    private string _saveAsFolderPath = string.Empty;

    [ObservableProperty]
    private string? _saveAsError;

    /// <summary>Available folder paths for the Save As location dropdown, set by the editor VM.</summary>
    public IReadOnlyList<string> AvailableFolders { get; internal set; } = [];

    /// <summary>Absolute path of the open collection root. Used to resolve relative SaveAsFolderPath values.</summary>
    public string CollectionRootPath { get; internal set; } = string.Empty;

    /// <summary>
    /// Names of all requests in the open collection.
    /// Used to populate the request picker in the dynamic value config dialog.
    /// Set by <see cref="RequestEditorViewModel"/> when the collection is loaded.
    /// </summary>
    public IReadOnlyList<string> AvailableRequestNames { get; internal set; } = [];

    // -------------------------------------------------------------------------
    // Close guard (shown when closing a dirty existing tab)
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private bool _pendingClose;

    // -------------------------------------------------------------------------
    // Response state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedResponseBody))]
    [NotifyPropertyChangedFor(nameof(ResponseLanguage))]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(ElapsedDisplay))]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeColor))]
    private ResponseModel? _response;

    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>True when the displayed response was loaded from history, not from a live send.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryResponseDisplay))]
    private bool _isResponseFromHistory;

    /// <summary>The timestamp of the history entry that was loaded, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryResponseDisplay))]
    private DateTimeOffset? _historyResponseDate;

    public string HistoryResponseDisplay =>
        IsResponseFromHistory && HistoryResponseDate is not null
            ? $"Loaded from history ({HistoryResponseDate.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss})"
            : string.Empty;

    // -------------------------------------------------------------------------
    // Key-value editors
    // -------------------------------------------------------------------------

    public KeyValueEditorViewModel Headers { get; } = new();
    public KeyValueEditorViewModel QueryParams { get; } = new();
    public KeyValueEditorViewModel PathParams { get; } = new();
    public KeyValueEditorViewModel FormParams { get; } = new();

    // -------------------------------------------------------------------------
    // Segment-editing dialogs (shared by all KV editors and auth fields)
    // -------------------------------------------------------------------------

    private TaskCompletionSource<DynamicValueSegment?>? _pendingDynamicConfigTcs;
    private TaskCompletionSource<MockDataSegment?>?     _pendingMockDataConfigTcs;

    /// <summary>
    /// Set just before <see cref="ShowDynamicValueConfig"/> becomes true.
    /// The view code-behind shows the dialog and calls <see cref="OnDynamicConfigDialogClosed"/> when done.
    /// </summary>
    public DynamicValueConfigViewModel? PendingDynamicConfig { get; private set; }

    /// <summary>Set just before <see cref="ShowMockDataConfig"/> becomes true.</summary>
    public MockDataConfigViewModel? PendingMockDataConfig { get; private set; }

    [ObservableProperty] private bool _showDynamicValueConfig;
    [ObservableProperty] private bool _showMockDataConfig;

    // -------------------------------------------------------------------------
    // cURL dialog state
    // -------------------------------------------------------------------------

    /// <summary>True when the cURL command dialog should be shown.</summary>
    [ObservableProperty] private bool _showCurlDialog;

    /// <summary>The fully-resolved request to display in the cURL dialog. Set just before <see cref="ShowCurlDialog"/> becomes true.</summary>
    internal RequestModel? CurlRequestSnapshot { get; private set; }

    /// <summary>API-key masking hints for the cURL dialog. Set alongside <see cref="CurlRequestSnapshot"/>.</summary>
    internal CurlAuthMaskInfo? CurlAuthMask { get; private set; }

    /// <summary>
    /// Opens the dynamic value configuration dialog. Returns the configured segment or null.
    /// Used as the callback wired into every KV editor row's <see cref="SegmentedValueFieldViewModel"/>.
    /// </summary>
    internal Task<DynamicValueSegment?> OpenDynamicValueConfigAsync(DynamicValueSegment? existing)
    {
        // This method is only reachable when _dynamicEvaluator is not null
        // (callbacks are only wired when the evaluator is available).
        if (_dynamicEvaluator is null)
            return Task.FromResult<DynamicValueSegment?>(null);

        _pendingDynamicConfigTcs?.TrySetResult(null);

        var staticVars = BuildMergedVars();

        PendingDynamicConfig = new DynamicValueConfigViewModel(
            _dynamicEvaluator,
            CollectionRootPath,
            string.Empty,            // no single env file in this context
            AvailableRequestNames,
            [],                      // env variables not needed here
            staticVars,
            existing);

        _pendingDynamicConfigTcs = new TaskCompletionSource<DynamicValueSegment?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ShowDynamicValueConfig = true;
        return _pendingDynamicConfigTcs.Task;
    }

    /// <summary>Called by the view's code-behind when the dynamic value dialog closes.</summary>
    internal void OnDynamicConfigDialogClosed()
    {
        ShowDynamicValueConfig = false;
        var result = PendingDynamicConfig?.IsConfirmed == true
            ? PendingDynamicConfig.ResultSegment
            : null;
        _pendingDynamicConfigTcs?.TrySetResult(result);
        _pendingDynamicConfigTcs = null;
        PendingDynamicConfig = null;
    }

    /// <summary>Opens the mock data picker dialog. Returns the configured segment or null.</summary>
    internal Task<MockDataSegment?> OpenMockDataConfigAsync(MockDataSegment? existing)
    {
        _pendingMockDataConfigTcs?.TrySetResult(null);

        PendingMockDataConfig = new MockDataConfigViewModel(existing);

        _pendingMockDataConfigTcs = new TaskCompletionSource<MockDataSegment?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ShowMockDataConfig = true;
        return _pendingMockDataConfigTcs.Task;
    }

    /// <summary>Called by the view's code-behind when the mock data dialog closes.</summary>
    internal void OnMockDataConfigDialogClosed()
    {
        ShowMockDataConfig = false;
        var result = PendingMockDataConfig?.IsConfirmed == true
            ? PendingMockDataConfig.ResultSegment
            : null;
        _pendingMockDataConfigTcs?.TrySetResult(result);
        _pendingMockDataConfigTcs = null;
        PendingMockDataConfig = null;
    }

    // -------------------------------------------------------------------------
    // Environment variable completions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Names of all variables in the currently active environment.
    /// Bound to <c>EnvVarCompletion.Suggestions</c> on URL, body, and auth TextBoxes.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<EnvVarSuggestion> _envVarNames = [];

    // -------------------------------------------------------------------------
    // Derived display properties
    // -------------------------------------------------------------------------

    public string StatusDisplay =>
        Response is null ? string.Empty : $"{Response.StatusCode} {Response.ReasonPhrase}";

    public string ElapsedDisplay =>
        Response is null ? string.Empty : $"{Response.Elapsed.TotalMilliseconds:F0} ms";

    public string SizeDisplay => Response is null
        ? string.Empty
        : Response.BodySizeBytes switch
        {
            < 1024 => $"{Response.BodySizeBytes} B",
            < 1024 * 1024 => $"{Response.BodySizeBytes / 1024.0:F1} KB",
            _ => $"{Response.BodySizeBytes / (1024.0 * 1024):F1} MB",
        };

    public string StatusBadgeColor => Response?.StatusCode switch
    {
        >= 200 and < 300 => "#1a5c33",
        >= 300 and < 400 => "#6b5800",
        >= 400 and < 500 => "#7a1e1e",
        >= 500 => "#5a1414",
        _ => "#0e639c",
    };

    public string MethodColor => HttpMethodColors.Hex(SelectedMethod);

    public string MethodPillLabel => SelectedMethod switch
    {
        "DELETE"  => "DEL",
        "OPTIONS" => "OPT",
        "PATCH"   => "PTCH",
        var m     => m,
    };

    public bool HasUnresolvedPathParams =>
        PathParams.Items.Any(item =>
            !string.IsNullOrWhiteSpace(item.Key) &&
            string.IsNullOrWhiteSpace(item.Value));

    public string PreviewUrlForeground => HasUnresolvedPathParams ? "#c07a20" : "#888888";

    public string PreviewUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Url))
                return string.Empty;

            var vars = BuildMergedVars();

            // Substitute {{tokens}} in path param values BEFORE URL-encoding.
            var pathParamValues = PathParams.GetEnabledPairs()
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .ToDictionary(
                    p => p.Key,
                    p => VariableSubstitutionService.Substitute(p.Value, vars) ?? p.Value);

            var baseUrl = GetBaseUrl(Url);
            var resolved = PathTemplateHelper.ApplyPathParams(baseUrl, pathParamValues);

            var substitutedQueryParams = QueryParams.GetEnabledPairs()
                .Select(p => new KeyValuePair<string, string>(
                    VariableSubstitutionService.Substitute(p.Key, vars) ?? p.Key,
                    VariableSubstitutionService.Substitute(p.Value, vars) ?? p.Value))
                .ToList();

            resolved = QueryStringHelper.ApplyQueryParams(resolved, substitutedQueryParams);

            return VariableSubstitutionService.Substitute(resolved, vars) ?? resolved;
        }
    }

    public bool ShowBodyEditor => SelectedBodyType != CollectionRequest.BodyTypes.None;
    public bool ShowTextBodyEditor => ShowBodyEditor && SelectedBodyType != CollectionRequest.BodyTypes.Form;
    public bool ShowFormBodyEditor => SelectedBodyType == CollectionRequest.BodyTypes.Form;
    public bool IsBodyJson => SelectedBodyType == CollectionRequest.BodyTypes.Json;

    /// <summary>Language hint for the request body editor (for syntax highlighting).</summary>
    public string BodyLanguage => SelectedBodyType switch
    {
        CollectionRequest.BodyTypes.Json => "json",
        CollectionRequest.BodyTypes.Xml  => "xml",
        _                                => string.Empty,
    };

    /// <summary>Language hint for the response body viewer derived from the Content-Type header.</summary>
    public string ResponseLanguage
    {
        get
        {
            if (Response is null) return string.Empty;
            var ct = Response.Headers.TryGetValue("Content-Type", out var v) ? v : string.Empty;
            if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)) return "json";
            if (ct.Contains("xml",  StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("xhtml", StringComparison.OrdinalIgnoreCase)) return "xml";
            if (ct.Contains("html", StringComparison.OrdinalIgnoreCase)) return "html";
            return string.Empty;
        }
    }

    /// <summary>
    /// The response body formatted according to the response Content-Type header.
    /// JSON and XML bodies are pretty-printed; all others are returned as-is.
    /// </summary>
    public string FormattedResponseBody
    {
        get
        {
            if (Response is null) return string.Empty;
            var contentType = Response.Headers.TryGetValue("Content-Type", out var ct) ? ct : null;
            return ResponseFormatter.FormatBody(Response.Body, contentType);
        }
    }

    public bool IsAuthBearer => AuthType == AuthConfig.AuthTypes.Bearer;
    public bool IsAuthBasic  => AuthType == AuthConfig.AuthTypes.Basic;
    public bool IsAuthApiKey => AuthType == AuthConfig.AuthTypes.ApiKey;

    // -------------------------------------------------------------------------
    // ComboBox source lists
    // -------------------------------------------------------------------------

    public IReadOnlyList<string> HttpMethods { get; } =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public IReadOnlyList<string> BodyTypes { get; } =
    [
        CollectionRequest.BodyTypes.None,
        CollectionRequest.BodyTypes.Json,
        CollectionRequest.BodyTypes.Text,
        CollectionRequest.BodyTypes.Xml,
        CollectionRequest.BodyTypes.Form,
        CollectionRequest.BodyTypes.Multipart,
    ];

    public IReadOnlyList<string> AuthTypes { get; } =
    [
        AuthConfig.AuthTypes.None,
        AuthConfig.AuthTypes.Bearer,
        AuthConfig.AuthTypes.Basic,
        AuthConfig.AuthTypes.ApiKey,
    ];

    public IReadOnlyList<string> ApiKeyLocations { get; } =
    [
        AuthConfig.ApiKeyLocations.Header,
        AuthConfig.ApiKeyLocations.Query,
    ];

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public RequestTabViewModel(
        ITransportRegistry transportRegistry,
        ICollectionService collectionService,
        IMessenger messenger,
        Action<RequestTabViewModel> requestClose,
        IDynamicVariableEvaluator? dynamicEvaluator = null,
        IHistoryService? historyService = null)
    {
        ArgumentNullException.ThrowIfNull(transportRegistry);
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(messenger);
        ArgumentNullException.ThrowIfNull(requestClose);

        _transportRegistry = transportRegistry;
        _collectionService = collectionService;
        _dynamicEvaluator = dynamicEvaluator;
        _messenger = messenger;
        _requestClose = requestClose;
        _historyService = historyService;

        // Initialize auth segment fields — sync back to plain string properties.
        SegmentedValueFieldViewModel? authTokenField = null;
        authTokenField = new SegmentedValueFieldViewModel(
            onChanged: () => { AuthToken = authTokenField!.GetInlineText(); });
        AuthTokenField = authTokenField;

        SegmentedValueFieldViewModel? authUsernameField = null;
        authUsernameField = new SegmentedValueFieldViewModel(
            onChanged: () => { AuthUsername = authUsernameField!.GetInlineText(); });
        AuthUsernameField = authUsernameField;

        SegmentedValueFieldViewModel? authPasswordField = null;
        authPasswordField = new SegmentedValueFieldViewModel(
            onChanged: () => { AuthPassword = authPasswordField!.GetInlineText(); });
        AuthPasswordField = authPasswordField;

        SegmentedValueFieldViewModel? authApiKeyNameField = null;
        authApiKeyNameField = new SegmentedValueFieldViewModel(
            onChanged: () => { AuthApiKeyName = authApiKeyNameField!.GetInlineText(); });
        AuthApiKeyNameField = authApiKeyNameField;

        SegmentedValueFieldViewModel? authApiKeyValueField = null;
        authApiKeyValueField = new SegmentedValueFieldViewModel(
            onChanged: () => { AuthApiKeyValue = authApiKeyValueField!.GetInlineText(); });
        AuthApiKeyValueField = authApiKeyValueField;

        PathParams.ShowAddButton = false;
        PathParams.ShowDeleteButton = false;
        PathParams.ShowEnabledToggle = false;

        // Note: key-pill editing has been removed from request fields.
        // Headers and query params still support {{var}} references via plain TextBoxes.

        // Dirty tracking — only fires for deliberate user edits to existing requests.
        // The _saving guard prevents reactive URL/param sync that occurs during PerformSaveAsync
        // from spuriously re-marking the request as dirty after HasUnsavedChanges is cleared.
        PropertyChanged += (_, e) =>
        {
            if (_loading || _saving || _sourceRequest is null) return;
            if (e.PropertyName is
                nameof(HasUnsavedChanges) or nameof(IsNew) or nameof(IsActive) or nameof(TabTitle) or
                nameof(TabIsDirty) or nameof(SaveButtonLabel) or
                nameof(SourceFilePath) or
                nameof(ShowSaveAsPanel) or nameof(SaveAsName) or nameof(SaveAsFolderPath) or
                nameof(SaveAsError) or nameof(PendingClose) or
                nameof(Response) or nameof(IsSending) or nameof(ErrorMessage) or
                nameof(IsResponseFromHistory) or nameof(HistoryResponseDate) or nameof(HistoryResponseDisplay) or
                nameof(StatusDisplay) or nameof(ElapsedDisplay) or nameof(SizeDisplay) or
                nameof(StatusBadgeColor) or nameof(MethodColor) or
                nameof(ShowBodyEditor) or nameof(ShowTextBodyEditor) or nameof(ShowFormBodyEditor) or
                nameof(PreviewUrl) or
                nameof(HasUnresolvedPathParams) or nameof(PreviewUrlForeground) or
                nameof(IsAuthBearer) or nameof(IsAuthBasic) or nameof(IsAuthApiKey) or
                nameof(EnvVarNames) or
                nameof(FormattedResponseBody) or nameof(IsBodyJson) or
                nameof(BodyLanguage) or nameof(ResponseLanguage) or
                nameof(ShowDynamicValueConfig) or nameof(ShowMockDataConfig) or
                nameof(ShowCurlDialog))
                return;
            HasUnsavedChanges = true;
        };

        Headers.Changed += (_, _) =>
        {
            if (!_loading && !_saving && _sourceRequest is not null) HasUnsavedChanges = true;
        };

        QueryParams.Changed += (_, _) =>
        {
            if (!_loading && !_saving && _sourceRequest is not null) HasUnsavedChanges = true;
            RebuildUrlFromParams();
            OnPropertyChanged(nameof(PreviewUrl));
        };

        PathParams.Changed += (_, _) =>
        {
            if (!_loading && !_saving && _sourceRequest is not null) HasUnsavedChanges = true;
            RebuildUrlFromPathParamNames();
            OnPropertyChanged(nameof(PreviewUrl));
            OnPropertyChanged(nameof(HasUnresolvedPathParams));
            OnPropertyChanged(nameof(PreviewUrlForeground));
        };

        FormParams.Changed += (_, _) =>
        {
            if (!_loading && !_saving && _sourceRequest is not null) HasUnsavedChanges = true;
        };
    }

    // -------------------------------------------------------------------------
    // Public API called by RequestEditorViewModel
    // -------------------------------------------------------------------------

    /// <summary>Loads a saved request into this tab.</summary>
    public void LoadRequest(CollectionRequest req)
    {
        _sourceRequest = req;
        _loading = true;
        try
        {
            RequestName = req.Name;
            OnPropertyChanged(nameof(CanOpenRequestHistory));
            SelectedMethod = req.Method.Method;

            _syncingUrl = true;
            try
            {
                var enabledParams = req.QueryParams
                    .Where(p => p.IsEnabled)
                    .Select(p => new KeyValuePair<string, string>(p.Key, p.Value))
                    .ToList();
                Url = enabledParams.Count > 0
                    ? QueryStringHelper.ApplyQueryParams(req.Url, enabledParams)
                    : req.Url;
                QueryParams.LoadFrom(req.QueryParams);
                SyncPathParamsWithUrl(Url, req.PathParams);
            }
            finally
            {
                _syncingUrl = false;
            }

            Headers.LoadFrom(req.Headers);
            SelectedBodyType = req.BodyType;
            Body = req.Body ?? string.Empty;
            FormParams.LoadFrom(req.FormParams);
            AuthType = req.Auth.AuthType;
            AuthToken = req.Auth.Token ?? string.Empty;
            AuthTokenField.LoadFromText(AuthToken);
            AuthUsername = req.Auth.Username ?? string.Empty;
            AuthUsernameField.LoadFromText(AuthUsername);
            AuthPassword = req.Auth.Password ?? string.Empty;
            AuthPasswordField.LoadFromText(AuthPassword);
            AuthApiKeyName = req.Auth.ApiKeyName ?? string.Empty;
            AuthApiKeyNameField.LoadFromText(AuthApiKeyName);
            AuthApiKeyValue = req.Auth.ApiKeyValue ?? string.Empty;
            AuthApiKeyValueField.LoadFromText(AuthApiKeyValue);
            AuthApiKeyIn = req.Auth.ApiKeyIn;
            Response = null;
            IsResponseFromHistory = false;
            HistoryResponseDate = null;
            ErrorMessage = null;
        }
        finally
        {
            _loading = false;
            HasUnsavedChanges = false;
            IsNew = false;
        }

        QueueHistoryResponseRefresh();
    }

    /// <summary>
    /// Updates the source request metadata after a rename.
    /// This ensures the tab title, session persistence, and dynamic cache all
    /// reflect the new request name and file path.
    /// </summary>
    internal void UpdateSourceRequest(CollectionRequest updated)
    {
        var wasDirty = HasUnsavedChanges;
        _loading = true;
        try
        {
            _sourceRequest = updated;
            OnPropertyChanged(nameof(CanOpenRequestHistory));
            RequestName = updated.Name;
        }
        finally
        {
            _loading = false;
            HasUnsavedChanges = wasDirty;
        }
        OnPropertyChanged(nameof(SourceFilePath));
    }

    /// <summary>Updates the active environment for variable substitution.</summary>
    public void SetEnvironment(EnvironmentModel? environment)
    {
        _activeEnvironment = environment;
        UpdateEnvSuggestions();
        QueueHistoryResponseRefresh();
    }

    /// <summary>Updates the global environment variables used as the baseline for substitution.</summary>
    public void SetGlobalEnvironment(EnvironmentModel environment)
    {
        _globalEnvironment = environment;
        UpdateEnvSuggestions();
    }

    /// <summary>
    /// Builds the merged variable dictionary for substitution.
    /// Global vars form the baseline; active environment vars take priority.
    /// </summary>
    private Dictionary<string, string> BuildMergedVars()
    {
        var merged = (_globalEnvironment.Variables).ToDictionary(v => v.Name, v => v.Value);
        if (_activeEnvironment is not null)
            foreach (var v in _activeEnvironment.Variables)
                merged[v.Name] = v.Value;
        return merged;
    }

    /// <summary>
    /// Async version of <see cref="BuildMergedVars"/> that additionally evaluates
    /// dynamic variables before returning. If no evaluator is registered, falls back
    /// to the static implementation.
    /// </summary>
    private async Task<ResolvedEnvironment> BuildMergedVarsAsync(CancellationToken ct)
    {
        var merged = BuildMergedVars();

        if (_dynamicEvaluator is null)
            return new ResolvedEnvironment { Variables = merged };

        var globalVars = _globalEnvironment.Variables;
        var globalHasDynamic = globalVars.Any(v =>
            v.VariableType is EnvironmentVariable.VariableTypes.Dynamic
                or EnvironmentVariable.VariableTypes.ResponseBody
                or EnvironmentVariable.VariableTypes.MockData);

        var activeVars = _activeEnvironment?.Variables ?? (IReadOnlyList<EnvironmentVariable>)[];
        var activeHasDynamic = activeVars.Any(v =>
            v.VariableType is EnvironmentVariable.VariableTypes.Dynamic
                or EnvironmentVariable.VariableTypes.ResponseBody
                or EnvironmentVariable.VariableTypes.MockData);

        if (!globalHasDynamic && !activeHasDynamic)
            return new ResolvedEnvironment { Variables = merged };

        var allMockGenerators = new Dictionary<string, MockDataEntry>();
        try
        {
            // 1. Resolve global dynamic vars first, passing the full merged static dict so that
            //    global requests can use active-env vars (e.g. baseUrl) when they need them.
            if (globalHasDynamic)
            {
                // Ensure a stable, per-collection cache key for the global environment
                // even if the model hasn't been fully loaded yet (FilePath could be empty
                // on first use before the environment editor has been opened).
                var globalFilePath = !string.IsNullOrEmpty(_globalEnvironment.FilePath)
                    ? _globalEnvironment.FilePath
                    : Path.Combine(CollectionRootPath, "environment", "_global.env.callsmith");

                // Scope the global var cache per active environment — a global token request
                // uses the active env's credentials/baseUrl, so each env gets its own token.
                var globalCacheNamespace = _activeEnvironment is not null
                    ? $"{globalFilePath}[env:{_activeEnvironment.FilePath}]"
                    : globalFilePath;

                var globalResolved = await _dynamicEvaluator.ResolveAsync(
                    CollectionRootPath,
                    globalCacheNamespace,
                    globalVars,
                    merged,
                    ct).ConfigureAwait(false);

                foreach (var kv in globalResolved.Variables)
                    merged[kv.Key] = kv.Value;
                foreach (var kv in globalResolved.MockGenerators)
                    allMockGenerators[kv.Key] = kv.Value;

                // Active-env static vars must still win over global resolved values.
                if (_activeEnvironment is not null)
                    foreach (var v in _activeEnvironment.Variables
                        .Where(v => v.VariableType == EnvironmentVariable.VariableTypes.Static
                                 && !string.IsNullOrWhiteSpace(v.Name)))
                        merged[v.Name] = v.Value;
            }

            // 2. Resolve active-env dynamic vars with global values now available in merged.
            if (activeHasDynamic && _activeEnvironment is not null)
            {
                var activeResolved = await _dynamicEvaluator.ResolveAsync(
                    CollectionRootPath,
                    _activeEnvironment.FilePath,
                    _activeEnvironment.Variables,
                    merged,
                    ct).ConfigureAwait(false);

                foreach (var kv in activeResolved.Variables)
                    merged[kv.Key] = kv.Value;
                foreach (var kv in activeResolved.MockGenerators)
                    allMockGenerators[kv.Key] = kv.Value;
            }

            return new ResolvedEnvironment { Variables = merged, MockGenerators = allMockGenerators };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // If evaluation fails, continue with static values so the request still sends.
            return new ResolvedEnvironment { Variables = merged };
        }
    }

    /// <summary>
    /// After a successful manual send, updates the dynamic variable cache for any
    /// response-body environment variables (in both global and active environments)
    /// that reference the request that was just executed.
    /// </summary>
    private async Task UpdateDynamicCacheFromResponseAsync(string responseBody, CancellationToken ct)
    {
        if (_dynamicEvaluator is null || _sourceRequest is null) return;

        var globalVars = _globalEnvironment.Variables;
        var globalFilePath = !string.IsNullOrEmpty(_globalEnvironment.FilePath)
            ? _globalEnvironment.FilePath
            : Path.Combine(CollectionRootPath, "environment", "_global.env.callsmith");

        // Global env: use the same env-scoped cache namespace as BuildMergedVarsAsync.
        var globalCacheNamespace = _activeEnvironment is not null
            ? $"{globalFilePath}[env:{_activeEnvironment.FilePath}]"
            : globalFilePath;

        try
        {
            await _dynamicEvaluator.UpdateCacheFromResponseAsync(
                CollectionRootPath,
                globalCacheNamespace,
                _sourceRequest.Name,
                responseBody,
                globalVars,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Cache update is best-effort; a miss just causes a re-execute on next send. */ }

        // Active environment: update using the active env's own cache namespace.
        if (_activeEnvironment is not null)
        {
            try
            {
                await _dynamicEvaluator.UpdateCacheFromResponseAsync(
                    CollectionRootPath,
                    _activeEnvironment.FilePath,
                    _sourceRequest.Name,
                    responseBody,
                    _activeEnvironment.Variables,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Cache update is best-effort; a miss just causes a re-execute on next send. */ }
        }
    }

    /// <summary>
    /// Rebuilds autocomplete suggestions from the merged global + active environment.
    /// Active environment values override global values for the same name.
    /// Secret variable values are shown as bullets.
    /// </summary>
    private void UpdateEnvSuggestions()
    {
        // Merge: global vars first, active env overrides
        var merged = new Dictionary<string, EnvironmentVariable>(StringComparer.Ordinal);
        foreach (var v in _globalEnvironment.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
            merged[v.Name] = v;
        if (_activeEnvironment is not null)
            foreach (var v in _activeEnvironment.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
                merged[v.Name] = v;

        var suggestions = merged.Values
            .Select(v => new EnvVarSuggestion(v.Name, v.IsSecret ? "\u2022\u2022\u2022\u2022\u2022" : v.Value))
            .ToList();

        EnvVarNames = suggestions;
        Headers.SetSuggestions(suggestions);
        QueryParams.SetSuggestions(suggestions);
        PathParams.SetSuggestions(suggestions);
        FormParams.SetSuggestions(suggestions);

        OnPropertyChanged(nameof(PreviewUrl));
    }

    // -------------------------------------------------------------------------
    // Commands — Body formatting
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pretty-prints the JSON body in the text editor.
    /// Does nothing if the body is not valid JSON.
    /// </summary>
    [RelayCommand]
    private void FormatBody()
    {
        var formatted = ResponseFormatter.TryFormatJson(Body);
        if (formatted is not null)
            Body = formatted;
    }

    [RelayCommand]
    private void OpenRequestHistory()
    {
        if (_sourceRequest?.RequestId is not { } requestId)
            return;

        _messenger.Send(new OpenHistoryMessage(requestId, RequestName));
    }

    // -------------------------------------------------------------------------
    // Commands — View as cURL
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the cURL command for the current request state with full variable resolution
    /// (including dynamic variables) — identical to what would actually be sent.
    /// </summary>
    [RelayCommand]
    private async Task ViewCurlAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Url)) return;

        var env = await BuildMergedVarsAsync(ct);

        // Resolve path params
        var pathParamValues = PathParams.GetEnabledPairs()
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(
                p => p.Key,
                p => VariableSubstitutionService.Substitute(p.Value, env) ?? p.Value);

        var baseUrl = GetBaseUrl(Url);
        var requestUrl = PathTemplateHelper.ApplyPathParams(baseUrl, pathParamValues);

        // Resolve query params
        var substitutedQueryParams = QueryParams.GetEnabledPairs()
            .Select(p => new KeyValuePair<string, string>(
                VariableSubstitutionService.Substitute(p.Key, env) ?? p.Key,
                VariableSubstitutionService.Substitute(p.Value, env) ?? p.Value))
            .ToList();

        requestUrl = QueryStringHelper.ApplyQueryParams(requestUrl, substitutedQueryParams);

        // Headers + auth
        var headers = ResolveHeaders(Headers.GetEnabledPairs(), env.Variables);

        ApplyAuthHeaders(headers, requestUrl, env.Variables, out requestUrl);

        requestUrl = VariableSubstitutionService.Substitute(requestUrl, env) ?? requestUrl;

        // Body
        string? resolvedBody = null;
        if (SelectedBodyType != CollectionRequest.BodyTypes.None &&
            SelectedBodyType != CollectionRequest.BodyTypes.Form &&
            !string.IsNullOrEmpty(Body))
            resolvedBody = VariableSubstitutionService.Substitute(Body, env) ?? Body;

        if (SelectedBodyType == CollectionRequest.BodyTypes.Form)
        {
            var formPairs = FormParams.GetEnabledPairs()
                .Select(p => new KeyValuePair<string, string>(
                    VariableSubstitutionService.Substitute(p.Key, env) ?? p.Key,
                    VariableSubstitutionService.Substitute(p.Value, env) ?? p.Value))
                .ToList();
            if (formPairs.Count > 0)
                resolvedBody = string.Join("&",
                    formPairs.Select(p =>
                        Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        }

        var request = new RequestModel
        {
            Method = new HttpMethod(SelectedMethod),
            Url = requestUrl,
            Headers = headers,
            Body = resolvedBody,
            ContentType = GetContentType(),
        };

        // Determine API-key masking info for the cURL dialog.
        CurlAuthMaskInfo? authMask = null;
        if (AuthType == AuthConfig.AuthTypes.ApiKey && !string.IsNullOrEmpty(AuthApiKeyName))
        {
            var resolvedName = VariableSubstitutionService.Substitute(AuthApiKeyName, env.Variables) ?? AuthApiKeyName;

            authMask = AuthApiKeyIn == AuthConfig.ApiKeyLocations.Header
                ? new CurlAuthMaskInfo(ApiKeyHeaderName: resolvedName, ApiKeyQueryParamName: null)
                : new CurlAuthMaskInfo(ApiKeyHeaderName: null, ApiKeyQueryParamName: resolvedName);
        }

        CurlRequestSnapshot = request;
        CurlAuthMask = authMask;
        ShowCurlDialog = true;
    }

    // -------------------------------------------------------------------------
    // Commands — Send
    // -------------------------------------------------------------------------

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Url)) return;

        IsSending = true;
        Response = null;
        ErrorMessage = null;

        try
        {
            var env = await BuildMergedVarsAsync(ct);
            var secretNames = BuildSecretVarNames();
            var sentBindings = new List<VariableBinding>();

            var headers = ResolveHeaders(Headers.GetEnabledPairs(), env.Variables, env.MockGenerators, secretNames, sentBindings);

            var pathParamValues = PathParams.GetEnabledPairs()
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .ToDictionary(
                    p => p.Key,
                    p => VariableSubstitutionService.SubstituteCollecting(p.Value, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Value);

            var baseUrl = GetBaseUrl(Url);
            var requestUrl = PathTemplateHelper.ApplyPathParams(baseUrl, pathParamValues);

            // Substitute variables in query param keys/values BEFORE URL-encoding.
            var substitutedQueryParams = QueryParams.GetEnabledPairs()
                .Select(p => new KeyValuePair<string, string>(
                    VariableSubstitutionService.SubstituteCollecting(p.Key, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Key,
                    VariableSubstitutionService.SubstituteCollecting(p.Value, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Value))
                .ToList();

            requestUrl = QueryStringHelper.ApplyQueryParams(requestUrl, substitutedQueryParams);

            ApplyAuthHeaders(headers, requestUrl, env.Variables, out requestUrl, env.MockGenerators, secretNames, sentBindings);

            // Substitute any remaining {{tokens}} in the base URL / path.
            requestUrl = VariableSubstitutionService.SubstituteCollecting(requestUrl, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? requestUrl;

            var resolvedBody = SelectedBodyType != CollectionRequest.BodyTypes.None
                && SelectedBodyType != CollectionRequest.BodyTypes.Form
                && !string.IsNullOrEmpty(Body)
                ? VariableSubstitutionService.SubstituteCollecting(Body, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? Body
                : null;

            // For form-encoded bodies, build the URL-encoded string from FormParams.
            if (SelectedBodyType == CollectionRequest.BodyTypes.Form)
            {
                var formPairs = FormParams.GetEnabledPairs()
                    .Select(p => new KeyValuePair<string, string>(
                        VariableSubstitutionService.SubstituteCollecting(p.Key, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Key,
                        VariableSubstitutionService.SubstituteCollecting(p.Value, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Value))
                    .ToList();
                if (formPairs.Count > 0)
                    resolvedBody = string.Join("&",
                        formPairs.Select(p =>
                            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            }

            var request = new RequestModel
            {
                Method = new HttpMethod(SelectedMethod),
                Url = requestUrl,
                Headers = headers,
                Body = resolvedBody,
                ContentType = GetContentType(),
            };

            var transport = _transportRegistry.Resolve(request);
            Response = await transport.SendAsync(request, ct);
            IsResponseFromHistory = false;
            HistoryResponseDate = null;
                var sentAt = DateTimeOffset.UtcNow - (Response?.Elapsed ?? TimeSpan.Zero);

                if (_historyService is not null && Response is not null)
                    _ = RecordHistoryAsync(env, Response, requestUrl, sentAt, sentBindings);

            // If this is a saved request, update the dynamic variable cache for any
            // environment variables that reference this request, so subsequent resolutions
            // pick up the fresh response without executing the request again.
            if (_dynamicEvaluator is not null
                && _sourceRequest is not null
                && !string.IsNullOrEmpty(Response?.Body))
            {
                await UpdateDynamicCacheFromResponseAsync(Response!.Body, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Request cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    // -------------------------------------------------------------------------
    // Commands — Save
    // -------------------------------------------------------------------------

    /// <summary>
    /// Saves the current tab. For existing requests, writes to disk immediately.
    /// For new (unsaved) tabs, opens the Save As panel.
    /// Always enabled so Ctrl+S works on both tab types.
    /// </summary>
    [RelayCommand]
    private Task SaveAsync(CancellationToken ct)
    {
        if (IsNew)
        {
            // Restore any previously-entered name into the panel.
            if (string.IsNullOrWhiteSpace(SaveAsName) || SaveAsName == "New Request")
                SaveAsName = string.IsNullOrWhiteSpace(RequestName) ? "New Request" : RequestName;

            // Auto-select the first available folder if none is chosen yet.
            if (string.IsNullOrEmpty(SaveAsFolderPath) && AvailableFolders.Count > 0)
                SaveAsFolderPath = AvailableFolders[0];

            SaveAsError = null;
            ShowSaveAsPanel = true;
            return Task.CompletedTask;
        }

        return PerformSaveAsync(ct);
    }

    [RelayCommand]
    private async Task CommitSaveAsAsync(CancellationToken ct)
    {
        var name = SaveAsName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SaveAsError = "Please enter a name.";
            return;
        }

        if (string.IsNullOrEmpty(CollectionRootPath))
        {
            SaveAsError = "Please open a collection before saving.";
            return;
        }

        // Sanitize filename
        var invalid = Path.GetInvalidFileNameChars();
        if (name.Any(c => invalid.Contains(c)))
        {
            SaveAsError = "Name contains invalid characters.";
            return;
        }

        var absoluteFolder = string.IsNullOrEmpty(SaveAsFolderPath)
            ? CollectionRootPath
            : Path.Combine(CollectionRootPath, SaveAsFolderPath);
        var filePath = Path.Combine(absoluteFolder, name + _collectionService.RequestFileExtension);
        if (File.Exists(filePath))
        {
            SaveAsError = $"\"{name}\" already exists in this folder.";
            return;
        }

        // Bootstrap _sourceRequest with the target path so PerformSaveAsync writes there.
        _sourceRequest = new CollectionRequest
        {
            FilePath = filePath,
            Name = name,
            Method = new HttpMethod(SelectedMethod),
            RequestId = Guid.NewGuid(),
            Url = string.Empty, // PerformSaveAsync will overwrite from editor state
            Headers = [],
            PathParams = new Dictionary<string, string>(),
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            Auth = new AuthConfig(),
        };

        RequestName = name;
        SaveAsError = null;
        ShowSaveAsPanel = false;
        IsNew = false;

        await PerformSaveAsync(ct);

        // Tell the sidebar to refresh so the new file appears in the tree.
        _messenger.Send(new CollectionRefreshRequestedMessage());
    }

    [RelayCommand]
    private void CancelSaveAs()
    {
        ShowSaveAsPanel = false;
        SaveAsError = null;
    }

    // -------------------------------------------------------------------------
    // Commands — Close + close guard
    // -------------------------------------------------------------------------

    /// <summary>
    /// Requests closing this tab. If the tab has unsaved changes to an existing
    /// request, shows the inline close guard. New tabs (never saved) close immediately.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        if (HasUnsavedChanges && !IsNew)
        {
            PendingClose = true;
            return;
        }
        _requestClose(this);
    }

    [RelayCommand]
    private async Task SaveAndCloseAsync(CancellationToken ct)
    {
        await PerformSaveAsync(ct);
        if (HasUnsavedChanges) return;  // save failed — keep the modal open
        PendingClose = false;
        _requestClose(this);
    }

    [RelayCommand]
    private void DiscardAndClose()
    {
        PendingClose = false;
        _requestClose(this);
    }

    [RelayCommand]
    private void CancelClose()
    {
        PendingClose = false;
    }

    // -------------------------------------------------------------------------
    // Shared save logic
    // -------------------------------------------------------------------------

    internal async Task PerformSaveAsync(CancellationToken ct = default)
    {
        if (_sourceRequest is null) return;

        var baseUrl = Url.Contains('?') ? Url[..Url.IndexOf('?')] : Url;

        var updated = new CollectionRequest
        {
            FilePath = _sourceRequest.FilePath,
            Name = RequestName,
            Method = new HttpMethod(SelectedMethod),
            Url = baseUrl,
            RequestId = _sourceRequest.RequestId ?? Guid.NewGuid(),
            Description = _sourceRequest.Description,
            Headers = Headers.GetAllKv(),
            PathParams = PathParams.GetEnabledPairs().ToDictionary(p => p.Key, p => p.Value),
            QueryParams = QueryParams.GetAllKv(),
            BodyType = SelectedBodyType,
            Body = string.IsNullOrEmpty(Body) ? null : Body,
            FormParams = FormParams.GetEnabledPairs().ToList(),
            Auth = new AuthConfig
            {
                AuthType = AuthType,
                Token = string.IsNullOrEmpty(AuthToken) ? null : AuthToken,
                Username = string.IsNullOrEmpty(AuthUsername) ? null : AuthUsername,
                Password = string.IsNullOrEmpty(AuthPassword) ? null : AuthPassword,
                ApiKeyName = string.IsNullOrEmpty(AuthApiKeyName) ? null : AuthApiKeyName,
                ApiKeyValue = string.IsNullOrEmpty(AuthApiKeyValue) ? null : AuthApiKeyValue,
                ApiKeyIn = AuthApiKeyIn,
            },
        };

        _saving = true;
        try
        {
            await _collectionService.SaveRequestAsync(updated, ct);
            _sourceRequest = updated;
            HasUnsavedChanges = false;
            ErrorMessage = null;
            OnPropertyChanged(nameof(CanOpenRequestHistory));
            _messenger.Send(new RequestSavedMessage(updated));
        }
        catch (OperationCanceledException)
        {
            // Save was cancelled — leave HasUnsavedChanges as-is so the user can retry.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            _saving = false;
        }
    }

    // -------------------------------------------------------------------------
    // URL <-> param sync
    // -------------------------------------------------------------------------

    partial void OnUrlChanged(string value)
    {
        if (_syncingUrl || _loading) return;
        _syncingUrl = true;
        try
        {
            var parsed = QueryStringHelper.ParseQueryParams(value);
            QueryParams.LoadFrom(parsed);
            var existingPathValues = PathParams.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Key))
                .ToDictionary(i => i.Key, i => i.Value);
            SyncPathParamsWithUrl(value, existingPathValues);
        }
        finally
        {
            _syncingUrl = false;
        }
    }

    private void RebuildUrlFromParams()
    {
        if (_syncingUrl || _loading) return;
        _syncingUrl = true;
        try
        {
            var pairs = QueryParams.GetEnabledPairs().ToList();
            Url = QueryStringHelper.ApplyQueryParams(Url, pairs);
        }
        finally
        {
            _syncingUrl = false;
        }
    }

    private void RebuildUrlFromPathParamNames()
    {
        if (_syncingUrl || _syncingPathParams || _loading) return;

        var currentBaseUrl = GetBaseUrl(Url);
        var existingNames = PathTemplateHelper.ExtractPathParamNames(currentBaseUrl);
        if (existingNames.Count == 0) return;

        var editedNames = PathParams.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .Select(i => i.Key)
            .ToList();

        if (editedNames.Count != existingNames.Count) return;

        var updatedBaseUrl = currentBaseUrl;
        for (var i = 0; i < existingNames.Count; i++)
        {
            if (string.Equals(existingNames[i], editedNames[i], StringComparison.Ordinal))
                continue;
            updatedBaseUrl = updatedBaseUrl.Replace(
                $"{{{existingNames[i]}}}", $"{{{editedNames[i]}}}",
                StringComparison.Ordinal);
        }

        if (string.Equals(updatedBaseUrl, currentBaseUrl, StringComparison.Ordinal)) return;

        _syncingUrl = true;
        try
        {
            Url = QueryStringHelper.ApplyQueryParams(updatedBaseUrl, QueryParams.GetEnabledPairs().ToList());
        }
        finally
        {
            _syncingUrl = false;
        }
    }

    // -------------------------------------------------------------------------
    // Auth helpers
    // -------------------------------------------------------------------------

    private static Dictionary<string, string> ResolveHeaders(
        IEnumerable<KeyValuePair<string, string>> source,
        IReadOnlyDictionary<string, string> vars,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators = null,
        IReadOnlySet<string>? secretVariableNames = null,
        IList<VariableBinding>? collector = null)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            string key, value;
            if (collector is not null && secretVariableNames is not null)
            {
                key = VariableSubstitutionService.SubstituteCollecting(pair.Key, vars, secretVariableNames, collector, mockGenerators) ?? pair.Key;
                value = VariableSubstitutionService.SubstituteCollecting(pair.Value, vars, secretVariableNames, collector, mockGenerators) ?? pair.Value;
            }
            else
            {
                key = VariableSubstitutionService.Substitute(pair.Key, vars) ?? pair.Key;
                value = VariableSubstitutionService.Substitute(pair.Value, vars) ?? pair.Value;
            }

            if (string.IsNullOrWhiteSpace(key)) continue;
            resolved[key] = value;
        }

        return resolved;
    }

    private void ApplyAuthHeaders(
        Dictionary<string, string> headers,
        string requestUrl,
        IReadOnlyDictionary<string, string> vars,
        out string url,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators = null,
        IReadOnlySet<string>? secretVariableNames = null,
        IList<VariableBinding>? collector = null)
    {
        url = requestUrl;

        string Resolve(string? template) =>
            collector is not null && secretVariableNames is not null
                ? VariableSubstitutionService.SubstituteCollecting(template, vars, secretVariableNames, collector, mockGenerators) ?? template ?? string.Empty
                : VariableSubstitutionService.Substitute(template, vars) ?? template ?? string.Empty;

        switch (AuthType)
        {
            case AuthConfig.AuthTypes.Bearer when !string.IsNullOrEmpty(AuthToken):
                var token = Resolve(AuthToken);
                headers["Authorization"] = $"Bearer {token}";
                break;
            case AuthConfig.AuthTypes.Basic when !string.IsNullOrEmpty(AuthUsername):
                var username = Resolve(AuthUsername);
                var password = Resolve(AuthPassword);
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{username}:{password}"));
                headers["Authorization"] = $"Basic {encoded}";
                break;
            case AuthConfig.AuthTypes.ApiKey when !string.IsNullOrEmpty(AuthApiKeyName)
                                               && !string.IsNullOrEmpty(AuthApiKeyValue):
                var resolvedName = Resolve(AuthApiKeyName);
                var resolvedValue = Resolve(AuthApiKeyValue);
                if (string.IsNullOrWhiteSpace(resolvedName))
                    break;

                if (AuthApiKeyIn == AuthConfig.ApiKeyLocations.Header)
                    headers[resolvedName] = resolvedValue;
                else
                    url = QueryStringHelper.AppendQueryParams(
                        requestUrl,
                        [new KeyValuePair<string, string>(resolvedName, resolvedValue)]);
                break;
        }
    }

    private static string GetBaseUrl(string value) => QueryStringHelper.GetBaseUrl(value);

    private void SyncPathParamsWithUrl(string url, IReadOnlyDictionary<string, string> existingValues)
    {
        _syncingPathParams = true;
        try
        {
            var names = PathTemplateHelper.ExtractPathParamNames(url);
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in names)
                merged[name] = existingValues.TryGetValue(name, out var value) ? value : string.Empty;
            PathParams.LoadFrom(merged);
        }
        finally
        {
            _syncingPathParams = false;
        }
    }

    private void QueueHistoryResponseRefresh()
    {
        var hydrationVersion = Interlocked.Increment(ref _historyHydrationVersion);

        Dispatcher.UIThread.Post(() =>
        {
            if (hydrationVersion != Interlocked.Read(ref _historyHydrationVersion))
                return;

            Response = null;
            IsResponseFromHistory = false;
            HistoryResponseDate = null;

            if (_historyService is null || _sourceRequest?.RequestId is not { } requestId)
                return;

            _ = HydrateResponseFromHistoryAsync(
                requestId,
                _activeEnvironment?.EnvironmentId,
                hydrationVersion);
        }, DispatcherPriority.Background);
    }

    private async Task HydrateResponseFromHistoryAsync(
        Guid requestId,
        Guid? environmentId,
        long hydrationVersion)
    {
        try
        {
            // Run history query on thread pool to avoid blocking UI during app startup when
            // multiple tabs are being restored. Add a small delay to stagger concurrent requests.
            await Task.Delay(10).ConfigureAwait(false);
            
            var latest = await _historyService!
                .GetLatestForRequestInEnvironmentAsync(requestId, environmentId, CancellationToken.None)
                .ConfigureAwait(false);

            if (hydrationVersion != Interlocked.Read(ref _historyHydrationVersion))
                return;

            if (latest?.ResponseSnapshot is null)
                return;

            var snapshot = latest.ResponseSnapshot;
            var response = new ResponseModel
            {
                StatusCode = snapshot.StatusCode,
                ReasonPhrase = snapshot.ReasonPhrase,
                Headers = snapshot.Headers,
                Body = snapshot.Body,
                BodyBytes = System.Text.Encoding.UTF8.GetBytes(snapshot.Body ?? string.Empty),
                FinalUrl = snapshot.FinalUrl,
                Elapsed = TimeSpan.FromMilliseconds(snapshot.ElapsedMs),
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (hydrationVersion != Interlocked.Read(ref _historyHydrationVersion))
                    return;

                Response = response;
                IsResponseFromHistory = true;
                HistoryResponseDate = latest.SentAt;
            });
        }
        catch
        {
            // Non-critical: if hydration fails, tab still opens with a clean empty response pane.
        }
    }

    private string? GetContentType() => SelectedBodyType switch
    {
        CollectionRequest.BodyTypes.Json => "application/json",
        CollectionRequest.BodyTypes.Text => "text/plain",
        CollectionRequest.BodyTypes.Xml  => "application/xml",
        CollectionRequest.BodyTypes.Form => "application/x-www-form-urlencoded",
        CollectionRequest.BodyTypes.Multipart => "multipart/form-data",
        _ => null,
    };

    // -------------------------------------------------------------------------
    // History helpers
    // -------------------------------------------------------------------------

    private async Task RecordHistoryAsync(
        ResolvedEnvironment env,
        ResponseModel response,
        string resolvedUrl,
        DateTimeOffset sentAt,
        IReadOnlyList<VariableBinding>? sentBindings = null)
    {
        try
        {
            var secretNames = BuildSecretVarNames();
            var bindings = new List<VariableBinding>();

            // Seed with the bindings captured at send time so that mock-data and
            // response-body variables reflect the exact values that were transmitted.
            if (sentBindings is not null)
                bindings.AddRange(sentBindings);

            // Also collect from all fields (including disabled ones) using only the
            // static/pre-resolved variables — no mock generators — so that disabled
            // fields are documented but mock variables are not re-generated here.
            void Collect(string? template) =>
                VariableSubstitutionService.SubstituteCollecting(
                    template, env.Variables, secretNames, bindings, mockGenerators: null);

            Collect(Url);
            foreach (var p in QueryParams.GetAllKv()) { Collect(p.Key); Collect(p.Value); }
            foreach (var p in Headers.GetAllKv())    { Collect(p.Key); Collect(p.Value); }
            Collect(Body);
            foreach (var p in FormParams.GetAllKv())  { Collect(p.Key); Collect(p.Value); }
            foreach (var p in PathParams.GetEnabledPairs()) Collect(p.Value);
            Collect(AuthToken);
            Collect(AuthUsername);
            Collect(AuthPassword);
            Collect(AuthApiKeyName);
            Collect(AuthApiKeyValue);

            // Deduplicate — same token may appear in multiple fields.
            var dedupedBindings = bindings
                .GroupBy(b => b.Token)
                .Select(g => g.First())
                .ToList();

            var contentType = GetContentType();
            IReadOnlyList<RequestKv> autoAppliedHeaders = contentType is not null
                ? [new RequestKv("Content-Type", contentType)]
                : [];

            var snapshot = new ConfiguredRequestSnapshot
            {
                Method = SelectedMethod,
                Url = Url,
                Headers = Headers.GetAllKv(),
                AutoAppliedHeaders = autoAppliedHeaders,
                QueryParams = QueryParams.GetAllKv(),
                PathParams = PathParams.GetEnabledPairs()
                    .ToDictionary(p => p.Key, p => p.Value),
                BodyType = SelectedBodyType,
                Body = string.IsNullOrEmpty(Body) ? null : Body,
                FormParams = FormParams.GetAllKv()
                    .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
                    .ToList(),
                Auth = new AuthConfig
                {
                    AuthType = AuthType,
                    Token = string.IsNullOrEmpty(AuthToken) ? null : AuthToken,
                    Username = string.IsNullOrEmpty(AuthUsername) ? null : AuthUsername,
                    Password = string.IsNullOrEmpty(AuthPassword) ? null : AuthPassword,
                    ApiKeyName = string.IsNullOrEmpty(AuthApiKeyName) ? null : AuthApiKeyName,
                    ApiKeyValue = string.IsNullOrEmpty(AuthApiKeyValue) ? null : AuthApiKeyValue,
                    ApiKeyIn = AuthApiKeyIn,
                },
            };

            var collectionName = !string.IsNullOrEmpty(CollectionRootPath)
                ? Path.GetFileName(CollectionRootPath.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))
                : null;

            var entry = new HistoryEntry
            {
                RequestId = _sourceRequest?.RequestId,
                SentAt = sentAt,
                Method = SelectedMethod,
                StatusCode = response.StatusCode,
                ResolvedUrl = resolvedUrl,
                RequestName = _sourceRequest is not null ? RequestName : null,
                CollectionName = collectionName,
                EnvironmentName = _activeEnvironment?.Name,
                EnvironmentId = _activeEnvironment?.EnvironmentId,
                EnvironmentColor = _activeEnvironment?.Color,
                CollectionPath = string.IsNullOrEmpty(CollectionRootPath) ? null : CollectionRootPath,
                ElapsedMs = (long)response.Elapsed.TotalMilliseconds,
                ConfiguredSnapshot = snapshot,
                VariableBindings = dedupedBindings,
                ResponseSnapshot = ResponseSnapshot.FromResponseModel(response),
            };

            await _historyService!.RecordAsync(entry, CancellationToken.None);
        }
        catch
        {
            // History recording must never disrupt the request UX. Errors are swallowed here;
            // the repository implementation is responsible for its own internal logging.
        }
    }

    private HashSet<string> BuildSecretVarNames()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in _globalEnvironment.Variables)
            if (v.IsSecret && !string.IsNullOrWhiteSpace(v.Name))
                secrets.Add(v.Name);
        if (_activeEnvironment is not null)
            foreach (var v in _activeEnvironment.Variables)
                if (v.IsSecret && !string.IsNullOrWhiteSpace(v.Name))
                    secrets.Add(v.Name);
        return secrets;
    }
}
