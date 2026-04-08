using System.Net.Http;
using System.Text;
using Avalonia.Threading;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Bruno;
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
    private readonly IEnvironmentMergeService _mergeService;
    private readonly IMessenger _messenger;
    private readonly Action<RequestTabViewModel> _requestClose;
    private Action<RequestTabViewModel>? _requestGlobalCloseGuard;
    private readonly IHistoryService? _historyService;
    private readonly IEnvironmentService? _environmentService;

    /// <summary>Source request loaded from disk. Null for brand-new unsaved tabs.</summary>
    private CollectionRequest? _sourceRequest;

    private EnvironmentModel? _activeEnvironment;
    private EnvironmentModel _globalEnvironment = new() { FilePath = string.Empty, Name = "Global", Variables = [], EnvironmentId = Guid.NewGuid() };

    private bool _loading;
    private bool _saving;
    private bool _syncingUrl;
    private bool _syncingPathParams;
    private long _historyHydrationVersion;
    private bool _closeAfterSaveAs;

    /// <summary>
    /// Per-body-type content store. Keyed by <see cref="CollectionRequest.BodyTypes"/> constants.
    /// Updated when the user switches body types so that each type's editor content is preserved
    /// independently and restored immediately when the user switches back.
    /// </summary>
    private readonly Dictionary<string, string> _bodyContents = new(StringComparer.Ordinal);

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
    [NotifyPropertyChangedFor(nameof(ShowRevertButton))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
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

    /// <summary>
    /// True when the Revert button should be shown: an existing (non-new) tab with unsaved changes.
    /// </summary>
    public bool ShowRevertButton => HasUnsavedChanges && !IsNew;

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
    [NotifyPropertyChangedFor(nameof(PreviewUrlTooltip))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private string _requestName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBodyEditor))]
    [NotifyPropertyChangedFor(nameof(ShowTextBodyEditor))]
    [NotifyPropertyChangedFor(nameof(ShowFormBodyEditor))]
    [NotifyPropertyChangedFor(nameof(ShowFileBodyEditor))]
    [NotifyPropertyChangedFor(nameof(IsBodyJson))]
    [NotifyPropertyChangedFor(nameof(CanFormatBody))]
    [NotifyPropertyChangedFor(nameof(BodyLanguage))]
    [NotifyPropertyChangedFor(nameof(SelectedBodyTypeOption))]
    private string _selectedBodyType = CollectionRequest.BodyTypes.None;

    [ObservableProperty]
    private string _body = string.Empty;

    /// <summary>File path of the currently selected file body, for display purposes only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileBodySelected))]
    private string _selectedFilePath = string.Empty;

    /// <summary>Callback injected by the view code-behind to open the platform file picker.</summary>
    public Func<CancellationToken, Task<(byte[] Bytes, string Name, string Path)?>>? OpenFilePickerFunc { get; set; }

    /// <summary>Raw bytes of the currently loaded file body. Null when no file has been selected.</summary>
    private byte[]? _fileBodyBytes;

    /// <summary>Original file name of the currently loaded file body.</summary>
    private string? _fileBodyName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthInherit))]
    [NotifyPropertyChangedFor(nameof(IsAuthBearer))]
    [NotifyPropertyChangedFor(nameof(IsAuthBasic))]
    [NotifyPropertyChangedFor(nameof(IsAuthApiKey))]
    private string _authType = AuthConfig.AuthTypes.Inherit;

    [ObservableProperty] private string _authToken = string.Empty;
    [ObservableProperty] private string _authUsername = string.Empty;
    [ObservableProperty] private string _authPassword = string.Empty;
    [ObservableProperty] private bool _showAuthPassword = false;
    [ObservableProperty] private string _authApiKeyName = string.Empty;
    [ObservableProperty] private string _authApiKeyValue = string.Empty;
    [ObservableProperty] private bool _showAuthApiKeyValue = false;
    [ObservableProperty] private string _authApiKeyIn = AuthConfig.ApiKeyLocations.Header;

    /// <summary>Optional human-readable description for this request (shown in the Info tab).</summary>
    [ObservableProperty]
    private string? _description;

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
    [NotifyPropertyChangedFor(nameof(ShowRevertButton))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
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

    /// <summary>True when the open collection is a Bruno collection (uses colon path-param syntax).</summary>
    public bool IsBrunoCollection => BrunoDetector.IsBrunoCollection(CollectionRootPath);

    /// <summary>
    /// Hint text for the path params editor, adapting to the collection format.
    /// Bruno collections use <c>:variable</c> syntax; Callsmith uses <c>{variable}</c>.
    /// </summary>
    public string PathParamHintText => IsBrunoCollection
        ? "use :variable syntax in the URL to add path params"
        : "use {variable} syntax in the URL to add path params";

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
    [NotifyPropertyChangedFor(nameof(FormattedResponseHeaders))]
    [NotifyPropertyChangedFor(nameof(ResponseHeaderRows))]
    [NotifyPropertyChangedFor(nameof(HasResponseHeaders))]
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
    [NotifyPropertyChangedFor(nameof(HistoryResponseDisplay), nameof(HistoryResponseToolTip))]
    private bool _isResponseFromHistory;

    /// <summary>The timestamp of the history entry that was loaded, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryResponseDisplay), nameof(HistoryResponseToolTip))]
    private DateTimeOffset? _historyResponseDate;

    public string HistoryResponseDisplay =>
        IsResponseFromHistory && HistoryResponseDate is not null
            ? $"Loaded from history ({HistoryResponseDate.Value.LocalDateTime:G})"
            : string.Empty;

    public string HistoryResponseToolTip =>
        IsResponseFromHistory && HistoryResponseDate is not null
            ? HistoryResponseDate.Value.LocalDateTime.ToString("F")
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
    // Layout mode
    // -------------------------------------------------------------------------

    /// <summary>
    /// True when the request config and response panels are displayed side-by-side (horizontal).
    /// False means the request config is above the response (vertical).
    /// Defaults to true (horizontal). Synced from collection preferences by
    /// <see cref="RequestEditorViewModel"/> when a collection is opened.
    /// </summary>
    [ObservableProperty]
    private bool _isHorizontalLayout = true;

    /// <summary>
    /// Saved fraction (0.0–1.0) of the available width allocated to the request-config panel
    /// when the layout is horizontal.
    /// Null means the default 0.45 ratio has not been overridden.
    /// Applied by <see cref="RequestEditorViewModel"/> when a tab is built.
    /// </summary>
    [ObservableProperty]
    private double? _horizontalSplitterPosition;

    /// <summary>
    /// Saved fraction (0.0–1.0) of the available height allocated to the request-config panel
    /// when the layout is vertical.
    /// Null means the default 0.45 ratio has not been overridden.
    /// Applied by <see cref="RequestEditorViewModel"/> when a tab is built.
    /// </summary>
    [ObservableProperty]
    private double? _verticalSplitterPosition;

    /// <summary>
    /// Optional callback invoked when the user changes the layout via
    /// <see cref="ToggleLayoutCommand"/>. Wired by <see cref="RequestEditorViewModel"/>
    /// to persist the choice and sync all other open tabs.
    /// </summary>
    internal Action<bool>? LayoutChangedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when the user finishes dragging the splitter.
    /// Receives the new pixel position and the current orientation (true = horizontal).
    /// Wired by <see cref="RequestEditorViewModel"/> to persist the position and
    /// sync all other open tabs.
    /// </summary>
    internal Action<double, bool>? SplitterChangedCallback { get; set; }

    partial void OnIsHorizontalLayoutChanged(bool value) => LayoutChangedCallback?.Invoke(value);

    /// <summary>Toggles between horizontal (side-by-side) and vertical (stacked) layout.</summary>
    [RelayCommand]
    private void ToggleLayout() => IsHorizontalLayout = !IsHorizontalLayout;

    // -------------------------------------------------------------------------
    // cURL dialog state
    // -------------------------------------------------------------------------

    /// <summary>True when the cURL command dialog should be shown.</summary>
    [ObservableProperty] private bool _showCurlDialog;

    /// <summary>The fully-resolved request to display in the cURL dialog. Set just before <see cref="ShowCurlDialog"/> becomes true.</summary>
    internal RequestModel? CurlRequestSnapshot { get; private set; }

    /// <summary>API-key masking hints for the cURL dialog. Set alongside <see cref="CurlRequestSnapshot"/>.</summary>
    internal CurlAuthMaskInfo? CurlAuthMask { get; private set; }

    /// <summary>Resolved values of all secret environment variables for the cURL dialog. Set alongside <see cref="CurlRequestSnapshot"/>.</summary>
    internal IReadOnlySet<string> CurlSecretValues { get; private set; } = new HashSet<string>();

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

        var staticVars = _mergeService.BuildStaticMerge(_globalEnvironment, _activeEnvironment);

        PendingDynamicConfig = new DynamicValueConfigViewModel(
            _dynamicEvaluator,
            CollectionRootPath,
            string.Empty,            // no env context in this scope
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

    public string PreviewUrlTooltip => PreviewUrl.Contains("{{", StringComparison.Ordinal) && PreviewUrl.Contains("}}", StringComparison.Ordinal)
        ? "Preview URL contains dynamic variables that will be resolved when sent"
        : "Fully resolved preview URL";

    public string PreviewUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Url))
                return string.Empty;

            // Build variable values for preview: uses BuildStaticMerge for values but removes
            // any variable whose winning definition in the three-pass precedence order is a
            // non-static (dynamic) type.  This lets static vars with empty values substitute
            // normally while dynamic vars leave their {{tokens}} unmodified and un-urlencoded.
            var previewVars = BuildPreviewVars();

            // Substitute {{tokens}} in path param values BEFORE URL-encoding.
            // Skip any path param whose substituted value still contains an unresolved {{token}}
            // so the braces are not percent-encoded into the preview URL.
            var pathParamValues = PathParams.GetEnabledPairs()
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .Select(p => (p.Key, Substituted: VariableSubstitutionService.Substitute(p.Value, previewVars) ?? p.Value))
                .ToDictionary(t => t.Key, t => t.Substituted);

            var resolved = PathTemplateHelper.ApplyPathParams(Url, pathParamValues);

            // Build query string manually so unresolved {{tokens}} in keys/values are not
            // percent-encoded — they should appear as-is in the preview URL.
            var queryParts = QueryParams.GetEnabledPairs()
                .Select(p => (
                    Key: VariableSubstitutionService.Substitute(p.Key,   previewVars) ?? p.Key,
                    Val: VariableSubstitutionService.Substitute(p.Value, previewVars) ?? p.Value))
                .Select(t => (
                    EncodedKey: t.Key.Contains("{{", StringComparison.Ordinal) ? t.Key : Uri.EscapeDataString(t.Key),
                    EncodedVal: t.Val.Contains("{{", StringComparison.Ordinal) ? t.Val : Uri.EscapeDataString(t.Val)))
                .ToList();

            if (queryParts.Count > 0)
            {
                var qIdx = resolved.IndexOf('?');
                var baseUrl = qIdx >= 0 ? resolved[..qIdx] : resolved;
                resolved = baseUrl + "?" + string.Join("&", queryParts.Select(t => $"{t.EncodedKey}={t.EncodedVal}"));
            }

            return VariableSubstitutionService.Substitute(resolved, previewVars) ?? resolved;
        }
    }

    public bool ShowBodyEditor => SelectedBodyType != CollectionRequest.BodyTypes.None;
    public bool ShowTextBodyEditor => SelectedBodyType is
        CollectionRequest.BodyTypes.Json or CollectionRequest.BodyTypes.Xml or
        CollectionRequest.BodyTypes.Yaml or CollectionRequest.BodyTypes.Text or
        CollectionRequest.BodyTypes.Other;
    public bool ShowFormBodyEditor => SelectedBodyType is
        CollectionRequest.BodyTypes.Form or CollectionRequest.BodyTypes.Multipart;
    public bool ShowFileBodyEditor => SelectedBodyType == CollectionRequest.BodyTypes.File;
    public bool HasFileBodySelected => _fileBodyBytes is not null;
    public bool IsBodyJson => SelectedBodyType == CollectionRequest.BodyTypes.Json;

    /// <summary>True when the request body type supports the Format action (JSON, XML, YAML).</summary>
    public bool CanFormatBody => SelectedBodyType is
        CollectionRequest.BodyTypes.Json or
        CollectionRequest.BodyTypes.Xml  or
        CollectionRequest.BodyTypes.Yaml;

    /// <summary>Language hint for the request body editor (for syntax highlighting).</summary>
    public string BodyLanguage => SelectedBodyType switch
    {
        CollectionRequest.BodyTypes.Json => "json",
        CollectionRequest.BodyTypes.Xml  => "xml",
        CollectionRequest.BodyTypes.Yaml => "yaml",
        _                                => string.Empty,
    };

    /// <summary>Language hint for the response body viewer derived from the Content-Type header.</summary>
    public string ResponseLanguage
    {
        get
        {
            if (Response is null) return string.Empty;
            var ct = Response.Headers.TryGetValue(WellKnownHeaders.ContentType, out var v) ? v : string.Empty;
            if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)) return "json";
            if (ct.Contains("yaml", StringComparison.OrdinalIgnoreCase)) return "yaml";
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
            var contentType = Response.Headers.TryGetValue(WellKnownHeaders.ContentType, out var ct) ? ct : null;
            return ResponseFormatter.FormatBody(Response.Body, contentType);
        }
    }

    /// <summary>
    /// Response headers formatted as a readable string (one "Key: Value" per line).
    /// Empty string when there is no response or no headers.
    /// </summary>
    public string FormattedResponseHeaders
    {
        get
        {
            if (Response is null || Response.Headers.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in Response.Headers)
                sb.AppendLine($"{kv.Key}: {kv.Value}");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Response headers projected into rows for table rendering.
    /// </summary>
    public IReadOnlyList<ResponseHeaderRowViewModel> ResponseHeaderRows
    {
        get
        {
            if (Response is null || Response.Headers.Count == 0)
                return [];

            var rows = new List<ResponseHeaderRowViewModel>(Response.Headers.Count);
            var rowIndex = 0;
            foreach (var kv in Response.Headers)
                rows.Add(new ResponseHeaderRowViewModel(kv.Key, kv.Value, rowIndex++));
            return rows;
        }
    }

    public bool HasResponseHeaders => ResponseHeaderRows.Count > 0;

    public bool IsAuthInherit => AuthType == AuthConfig.AuthTypes.Inherit;
    public bool IsAuthBearer => AuthType == AuthConfig.AuthTypes.Bearer;
    public bool IsAuthBasic  => AuthType == AuthConfig.AuthTypes.Basic;
    public bool IsAuthApiKey => AuthType == AuthConfig.AuthTypes.ApiKey;

    // -------------------------------------------------------------------------
    // ComboBox source lists
    // -------------------------------------------------------------------------

    public IReadOnlyList<string> HttpMethods { get; } =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public IReadOnlyList<BodyTypeOption> BodyTypes { get; } =
    [
        new() { Value = CollectionRequest.BodyTypes.None,      DisplayName = "None"           },
        new() { Value = CollectionRequest.BodyTypes.Json,      DisplayName = "JSON"           },
        new() { Value = CollectionRequest.BodyTypes.Xml,       DisplayName = "XML"            },
        new() { Value = CollectionRequest.BodyTypes.Yaml,      DisplayName = "YAML"           },
        new() { Value = CollectionRequest.BodyTypes.Text,      DisplayName = "Text"           },
        new() { Value = CollectionRequest.BodyTypes.Other,     DisplayName = "Other"          },
        BodyTypeOption.Separator,
        new() { Value = CollectionRequest.BodyTypes.Multipart, DisplayName = "Multipart Form" },
        new() { Value = CollectionRequest.BodyTypes.Form,      DisplayName = "URL Encoded Form" },
        BodyTypeOption.Separator,
        new() { Value = CollectionRequest.BodyTypes.File,      DisplayName = "File"           },
    ];

    /// <summary>
    /// The currently-selected body type as a <see cref="BodyTypeOption"/>.
    /// The view reads this via a OneWay binding; user selections are written back through
    /// the <c>SelectionChanged</c> code-behind handler (see RequestView.axaml.cs).
    /// Separator items and null values are rejected in the setter.
    /// </summary>
    public BodyTypeOption? SelectedBodyTypeOption
    {
        get => BodyTypes.FirstOrDefault(o => !o.IsSeparator && o.Value == SelectedBodyType);
        set
        {
            if (value is null || value.IsSeparator) return;
            SelectedBodyType = value.Value;
        }
    }

    public IReadOnlyList<string> AuthTypes { get; } =
    [
        AuthConfig.AuthTypes.Inherit,
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
        IHistoryService? historyService = null,
        IEnvironmentService? environmentService = null,
        IEnvironmentMergeService? mergeService = null)
    {
        ArgumentNullException.ThrowIfNull(transportRegistry);
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(messenger);
        ArgumentNullException.ThrowIfNull(requestClose);

        _transportRegistry = transportRegistry;
        _collectionService = collectionService;
        _dynamicEvaluator = dynamicEvaluator;
        _mergeService = mergeService ?? new EnvironmentMergeService(dynamicEvaluator);
        _messenger = messenger;
        _requestClose = requestClose;
        _historyService = historyService;
        _environmentService = environmentService;

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
                nameof(TabIsDirty) or nameof(SaveButtonLabel) or nameof(ShowRevertButton) or
                nameof(SourceFilePath) or
                nameof(ShowSaveAsPanel) or nameof(SaveAsName) or nameof(SaveAsFolderPath) or
                nameof(SaveAsError) or nameof(PendingClose) or
                nameof(Response) or nameof(IsSending) or nameof(ErrorMessage) or
                nameof(IsResponseFromHistory) or nameof(HistoryResponseDate) or nameof(HistoryResponseDisplay) or nameof(HistoryResponseToolTip) or
                nameof(StatusDisplay) or nameof(ElapsedDisplay) or nameof(SizeDisplay) or
                nameof(StatusBadgeColor) or nameof(MethodColor) or
                nameof(ShowBodyEditor) or nameof(ShowTextBodyEditor) or nameof(ShowFormBodyEditor) or
                nameof(ShowFileBodyEditor) or nameof(HasFileBodySelected) or nameof(SelectedBodyTypeOption) or
                nameof(CanFormatBody) or
                nameof(PreviewUrl) or nameof(HasUnresolvedPathParams) or nameof(PreviewUrlForeground) or nameof(PreviewUrlTooltip) or
                nameof(IsAuthInherit) or nameof(IsAuthBearer) or nameof(IsAuthBasic) or nameof(IsAuthApiKey) or
                nameof(ShowAuthPassword) or nameof(ShowAuthApiKeyValue) or
                nameof(EnvVarNames) or
                nameof(FormattedResponseBody) or nameof(FormattedResponseHeaders) or
                nameof(ResponseHeaderRows) or nameof(HasResponseHeaders) or nameof(IsBodyJson) or
                nameof(BodyLanguage) or nameof(ResponseLanguage) or
                nameof(ShowDynamicValueConfig) or nameof(ShowMockDataConfig) or
                nameof(ShowCurlDialog) or
                nameof(IsHorizontalLayout) or
                nameof(HorizontalSplitterPosition) or
                nameof(VerticalSplitterPosition))
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
            OnPropertyChanged(nameof(PreviewUrl));
            OnPropertyChanged(nameof(PreviewUrlTooltip));
        };

        PathParams.Changed += (_, _) =>
        {
            if (!_loading && !_saving && _sourceRequest is not null) HasUnsavedChanges = true;
            RebuildUrlFromPathParamNames();
            OnPropertyChanged(nameof(PreviewUrl));
            OnPropertyChanged(nameof(HasUnresolvedPathParams));
            OnPropertyChanged(nameof(PreviewUrlForeground));
            OnPropertyChanged(nameof(PreviewUrlTooltip));
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
                Url = req.Url;
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
            // Restore file body if present.
            if (req.BodyType == CollectionRequest.BodyTypes.File && req.FileBodyBase64 is not null)
            {
                _fileBodyBytes = Convert.FromBase64String(req.FileBodyBase64);
                _fileBodyName = req.FileBodyName;
                SelectedFilePath = req.FileBodyName ?? string.Empty;
            }
            else
            {
                _fileBodyBytes = null;
                _fileBodyName = null;
                SelectedFilePath = string.Empty;
            }
            // Seed the per-type dictionary so switching body types immediately shows the
            // correct editor content without needing a tab reload.
            _bodyContents.Clear();
            foreach (var (type, content) in req.AllBodyContents)
                _bodyContents[type] = content;
            // Ensure the active type is up-to-date (AllBodyContents may have been built before
            // the ViewModel edits; this is the authoritative current value).
            if (req.BodyType is CollectionRequest.BodyTypes.Json
                             or CollectionRequest.BodyTypes.Text
                             or CollectionRequest.BodyTypes.Xml
                             or CollectionRequest.BodyTypes.Yaml
                             or CollectionRequest.BodyTypes.Other)
                _bodyContents[req.BodyType] = req.Body ?? string.Empty;
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
            Description = req.Description;
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
    /// Sets a callback used when this tab requests a close guard while dirty.
    /// When set, the host can present a global confirmation UI.
    /// </summary>
    internal void SetGlobalCloseGuardHandler(Action<RequestTabViewModel> handler)
    {
        _requestGlobalCloseGuard = handler;
    }

    /// <summary>
    /// Populates this tab with the fully-resolved field values from a history entry.
    /// Used when the user clicks "Resend Request" in the history panel.
    /// All variable bindings should already be revealed (secrets decrypted) before calling this.
    /// </summary>
    public void LoadFromHistorySnapshot(
        ConfiguredRequestSnapshot snapshot,
        IReadOnlyList<VariableBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(bindings);

        var vars = HistorySentViewBuilder.BuildVariableMap(bindings);

        // Resolve path params and apply to base URL so the URL field shows the literal value.
        var resolvedPathParams = snapshot.PathParams.ToDictionary(
            p => p.Key,
            p => VariableSubstitutionService.Substitute(p.Value, vars) ?? p.Value);
        var baseUrl = QueryStringHelper.GetBaseUrl(snapshot.Url);
        var resolvedUrl = VariableSubstitutionService.Substitute(baseUrl, vars) ?? baseUrl;

        // Resolve query params — only include enabled entries.
        var resolvedQueryParams = snapshot.QueryParams
            .Where(p => p.IsEnabled)
            .Select(p => new RequestKv(
                VariableSubstitutionService.Substitute(p.Key, vars) ?? p.Key,
                VariableSubstitutionService.Substitute(p.Value, vars) ?? p.Value,
                IsEnabled: true))
            .ToList<RequestKv>();

        // Resolve headers (user-authored only; auto-applied headers are not part of the editor).
        // Only include enabled entries — disabled headers are not carried over on resend.
        var resolvedHeaders = snapshot.Headers
            .Where(h => h.IsEnabled)
            .Select(h => new RequestKv(
                VariableSubstitutionService.Substitute(h.Key, vars) ?? h.Key,
                VariableSubstitutionService.Substitute(h.Value, vars) ?? h.Value,
                IsEnabled: true))
            .ToList<RequestKv>();

        // Auth mode is NOT preserved on resend. Instead, auth is materialised as explicit
        // headers or query params so the new tab shows only literal, active values.
        // Use EffectiveAuth when available so that inherited auth is correctly replayed.
        var auth = snapshot.EffectiveAuth ?? snapshot.Auth;
        switch (auth.AuthType)
        {
            case AuthConfig.AuthTypes.Bearer when !string.IsNullOrEmpty(auth.Token):
                var bearerToken = VariableSubstitutionService.Substitute(auth.Token, vars) ?? auth.Token;
                resolvedHeaders.Add(new RequestKv(WellKnownHeaders.Authorization, $"Bearer {bearerToken}", IsEnabled: true));
                break;
            case AuthConfig.AuthTypes.Basic when !string.IsNullOrEmpty(auth.Username):
                var username = VariableSubstitutionService.Substitute(auth.Username, vars) ?? auth.Username;
                var password = VariableSubstitutionService.Substitute(auth.Password, vars) ?? string.Empty;
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                resolvedHeaders.Add(new RequestKv(WellKnownHeaders.Authorization, $"Basic {encoded}", IsEnabled: true));
                break;
            case AuthConfig.AuthTypes.ApiKey when !string.IsNullOrEmpty(auth.ApiKeyName)
                                               && !string.IsNullOrEmpty(auth.ApiKeyValue):
                var keyName = VariableSubstitutionService.Substitute(auth.ApiKeyName, vars) ?? auth.ApiKeyName;
                var keyValue = VariableSubstitutionService.Substitute(auth.ApiKeyValue, vars) ?? auth.ApiKeyValue;
                if (!string.IsNullOrWhiteSpace(keyName))
                {
                    if (auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header)
                        resolvedHeaders.Add(new RequestKv(keyName, keyValue, IsEnabled: true));
                    else
                        resolvedQueryParams.Add(new RequestKv(keyName, keyValue, IsEnabled: true));
                }
                break;
        }

        // Resolve form params.
        var resolvedFormParams = snapshot.FormParams
            .Select(p => new KeyValuePair<string, string>(
                VariableSubstitutionService.Substitute(p.Key, vars) ?? p.Key,
                VariableSubstitutionService.Substitute(p.Value, vars) ?? p.Value))
            .ToList();

        // Resolve body.
        var resolvedBody = VariableSubstitutionService.Substitute(snapshot.Body, vars);

        // Restore file body bytes from the history snapshot.
        byte[]? restoredFileBytes = null;
        string? restoredFileName = null;
        if (snapshot.BodyType == CollectionRequest.BodyTypes.File
            && snapshot.FileBodyBase64 is not null)
        {
            restoredFileBytes = Convert.FromBase64String(snapshot.FileBodyBase64);
            restoredFileName = snapshot.FileBodyName;
        }

        // No source file — this is a brand-new unsaved tab.
        _sourceRequest = null;

        _loading = true;
        try
        {
            RequestName = string.Empty;
            OnPropertyChanged(nameof(CanOpenRequestHistory));
            SelectedMethod = snapshot.Method;

            _syncingUrl = true;
            try
            {
                var enabledParams = resolvedQueryParams
                    .Select(p => new KeyValuePair<string, string>(p.Key, p.Value))
                    .ToList();
                Url = resolvedUrl;
                QueryParams.LoadFrom(resolvedQueryParams);
                SyncPathParamsWithUrl(resolvedUrl, resolvedPathParams);
            }
            finally
            {
                _syncingUrl = false;
            }

            Headers.LoadFrom(resolvedHeaders);
            SelectedBodyType = snapshot.BodyType;
            Body = resolvedBody ?? string.Empty;
            _fileBodyBytes = restoredFileBytes;
            _fileBodyName = restoredFileName;
            SelectedFilePath = restoredFileName ?? string.Empty;
            FormParams.LoadFrom(resolvedFormParams);
            AuthType = AuthConfig.AuthTypes.Inherit;
            AuthToken = string.Empty;
            AuthTokenField.LoadFromText(AuthToken);
            AuthUsername = string.Empty;
            AuthUsernameField.LoadFromText(AuthUsername);
            AuthPassword = string.Empty;
            AuthPasswordField.LoadFromText(AuthPassword);
            AuthApiKeyName = string.Empty;
            AuthApiKeyNameField.LoadFromText(AuthApiKeyName);
            AuthApiKeyValue = string.Empty;
            AuthApiKeyValueField.LoadFromText(AuthApiKeyValue);
            AuthApiKeyIn = AuthConfig.ApiKeyLocations.Header;
            Response = null;
            IsResponseFromHistory = false;
            HistoryResponseDate = null;
            ErrorMessage = null;
        }
        finally
        {
            _loading = false;
            // HasUnsavedChanges = false mirrors the new-tab baseline: the user has not
            // made any edits yet.  IsNew = true ensures TabIsDirty is true (so "Save As…"
            // is shown) and the tab is excluded from session persistence.
            HasUnsavedChanges = false;
            IsNew = true;
        }
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
        OnPropertyChanged(nameof(PreviewUrl));
        OnPropertyChanged(nameof(PreviewUrlTooltip));
    }

    /// <summary>Updates the global environment variables used as the baseline for substitution.</summary>
    public void SetGlobalEnvironment(EnvironmentModel environment)
    {
        _globalEnvironment = environment;
        UpdateEnvSuggestions();
        OnPropertyChanged(nameof(PreviewUrl));
        OnPropertyChanged(nameof(PreviewUrlTooltip));
    }

    /// <summary>
    /// After a successful manual send, updates the dynamic variable cache for any
    /// response-body environment variables (in both global and active environments)
    /// that reference the request that was just executed.
    /// </summary>
    private async Task UpdateDynamicCacheFromResponseAsync(string responseBody, CancellationToken ct)
    {
        if (_dynamicEvaluator is null || _sourceRequest is null) return;

        // A stable requestId is needed to write a cache key that ResolveAsync can find.
        // If the source request has no ID (pre-dates the requestId migration), skip the update —
        // dynamic vars will simply re-execute on the next resolution.
        var requestId = _sourceRequest.RequestId;
        if (requestId is null) return;

        var globalVars = _globalEnvironment.Variables;

        // Global env: use the active environment's ID as the cache namespace (unified namespace)
        // so that cache entries are shared with the editor preview and the merge service.
        var globalCacheNamespace = _activeEnvironment is not null
            ? _activeEnvironment.EnvironmentId.ToString("N")
            : _globalEnvironment.EnvironmentId.ToString("N");

        try
        {
            await _dynamicEvaluator.UpdateCacheFromResponseAsync(
                CollectionRootPath,
                globalCacheNamespace,
                requestId.Value,
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
                    _activeEnvironment.EnvironmentId.ToString("N"),
                    requestId.Value,
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
        OnPropertyChanged(nameof(PreviewUrlTooltip));
    }

    // -------------------------------------------------------------------------
    // Commands — Body formatting and file selection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pretty-prints the body in the text editor based on the selected body type.
    /// Supports JSON, XML, and YAML. Does nothing if the body cannot be parsed.
    /// </summary>
    [RelayCommand]
    private void FormatBody()
    {
        var formatted = SelectedBodyType switch
        {
            CollectionRequest.BodyTypes.Json => ResponseFormatter.TryFormatJson(Body),
            CollectionRequest.BodyTypes.Xml  => ResponseFormatter.TryFormatXml(Body),
            CollectionRequest.BodyTypes.Yaml => ResponseFormatter.TryFormatYaml(Body),
            _                                => null,
        };
        if (formatted is not null)
            Body = formatted;
    }

    /// <summary>
    /// Opens the platform file picker and loads the selected file as the request body.
    /// Does nothing when <see cref="OpenFilePickerFunc"/> has not been set by the view.
    /// </summary>
    [RelayCommand]
    private async Task SelectFileAsync(CancellationToken ct)
    {
        if (OpenFilePickerFunc is null) return;
        var result = await OpenFilePickerFunc(ct);
        if (result is null) return;
        _fileBodyBytes = result.Value.Bytes;
        _fileBodyName = result.Value.Name;
        SelectedFilePath = result.Value.Path;
        OnPropertyChanged(nameof(HasFileBodySelected));
        if (_sourceRequest is not null && !_loading && !_saving)
            HasUnsavedChanges = true;
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

        var env = await _mergeService.MergeAsync(CollectionRootPath, _globalEnvironment, _activeEnvironment, ct: ct);

        // Resolve path params
        var pathParamValues = PathParams.GetEnabledPairs()
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(
                p => p.Key,
                p => VariableSubstitutionService.Substitute(p.Value, env) ?? p.Value);

        var requestUrl = PathTemplateHelper.ApplyPathParams(Url, pathParamValues);

        // Resolve query params
        var substitutedQueryParams = QueryParams.GetEnabledPairs()
            .Select(p => new KeyValuePair<string, string>(
                VariableSubstitutionService.Substitute(p.Key, env) ?? p.Key,
                VariableSubstitutionService.Substitute(p.Value, env) ?? p.Value))
            .ToList();

        requestUrl = QueryStringHelper.AppendQueryParams(requestUrl, substitutedQueryParams);

        // Headers + auth
        var headers = ResolveHeaders(Headers.GetEnabledPairs(), env.Variables);

        var effectiveAuth = await GetEffectiveAuthAsync(ct);
        ApplyAuthHeaders(headers, requestUrl, env.Variables, out requestUrl, effectiveAuth);

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
        if (effectiveAuth.AuthType == AuthConfig.AuthTypes.ApiKey && !string.IsNullOrEmpty(effectiveAuth.ApiKeyName))
        {
            var resolvedName = VariableSubstitutionService.Substitute(effectiveAuth.ApiKeyName, env.Variables) ?? effectiveAuth.ApiKeyName;

            authMask = effectiveAuth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header
                ? new CurlAuthMaskInfo(ApiKeyHeaderName: resolvedName, ApiKeyQueryParamName: null)
                : new CurlAuthMaskInfo(ApiKeyHeaderName: null, ApiKeyQueryParamName: resolvedName);
        }

        CurlRequestSnapshot = request;
        CurlAuthMask = authMask;

        // Collect the resolved values of all secret environment variables so the cURL
        // dialog can replace them with <secret> when masking is active.
        var secretVariableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _globalEnvironment.Variables.Where(v => v.IsSecret))
            secretVariableNames.Add(v.Name);
        if (_activeEnvironment is not null)
            foreach (var v in _activeEnvironment.Variables.Where(v => v.IsSecret))
                secretVariableNames.Add(v.Name);

        var secretValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in secretVariableNames)
        {
            if (env.Variables.TryGetValue(name, out var val) && !string.IsNullOrEmpty(val))
                secretValues.Add(val);
        }
        CurlSecretValues = secretValues;

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
            var env = await _mergeService.MergeAsync(CollectionRootPath, _globalEnvironment, _activeEnvironment, ct: ct);
            var secretNames = BuildSecretVarNames();
            var sentBindings = new List<VariableBinding>();

            var headers = ResolveHeaders(Headers.GetEnabledPairs(), env.Variables, env.MockGenerators, secretNames, sentBindings);

            var pathParamValues = PathParams.GetEnabledPairs()
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .ToDictionary(
                    p => p.Key,
                    p => VariableSubstitutionService.SubstituteCollecting(p.Value, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Value);

            var requestUrl = PathTemplateHelper.ApplyPathParams(Url, pathParamValues);

            // Substitute variables in query param keys/values BEFORE URL-encoding.
            var substitutedQueryParams = QueryParams.GetEnabledPairs()
                .Select(p => new KeyValuePair<string, string>(
                    VariableSubstitutionService.SubstituteCollecting(p.Key, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Key,
                    VariableSubstitutionService.SubstituteCollecting(p.Value, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Value))
                .ToList();

            requestUrl = QueryStringHelper.AppendQueryParams(requestUrl, substitutedQueryParams);

            var effectiveAuth = await GetEffectiveAuthAsync(ct).ConfigureAwait(false);
            ApplyAuthHeaders(headers, requestUrl, env.Variables, out requestUrl, effectiveAuth, env.MockGenerators, secretNames, sentBindings);

            // Substitute any remaining {{tokens}} in the base URL / path.
            requestUrl = VariableSubstitutionService.SubstituteCollecting(requestUrl, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? requestUrl;

            var resolvedBody = SelectedBodyType is
                CollectionRequest.BodyTypes.Json or CollectionRequest.BodyTypes.Xml or
                CollectionRequest.BodyTypes.Yaml or CollectionRequest.BodyTypes.Text or
                CollectionRequest.BodyTypes.Other
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

            // For multipart bodies, collect form params for proper MultipartFormDataContent.
            IReadOnlyList<KeyValuePair<string, string>>? multipartFormParams = null;
            if (SelectedBodyType == CollectionRequest.BodyTypes.Multipart)
            {
                multipartFormParams = FormParams.GetEnabledPairs()
                    .Select(p => new KeyValuePair<string, string>(
                        VariableSubstitutionService.SubstituteCollecting(p.Key, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Key,
                        VariableSubstitutionService.SubstituteCollecting(p.Value, env.Variables, secretNames, sentBindings, env.MockGenerators) ?? p.Value))
                    .ToList();
            }

            // For file bodies, pass the loaded bytes directly.
            byte[]? fileBodyBytes = SelectedBodyType == CollectionRequest.BodyTypes.File
                ? _fileBodyBytes
                : null;

            var request = new RequestModel
            {
                Method = new HttpMethod(SelectedMethod),
                Url = requestUrl,
                Headers = headers,
                Body = resolvedBody,
                BodyBytes = fileBodyBytes,
                MultipartFormParams = multipartFormParams,
                ContentType = GetContentType(),
            };

            var transport = _transportRegistry.Resolve(request);
            Response = await transport.SendAsync(request, ct);
            IsResponseFromHistory = false;
            HistoryResponseDate = null;
                var sentAt = DateTimeOffset.UtcNow - (Response?.Elapsed ?? TimeSpan.Zero);

                if (_historyService is not null && Response is not null)
                    _ = RecordHistoryAsync(env, Response, requestUrl, sentAt, effectiveAuth, sentBindings);

            // If this is a saved request, update the dynamic variable cache for any
            // environment variables that reference this request, so subsequent resolutions
            // pick up the fresh response without executing the request again.
            if (_dynamicEvaluator is not null
                && _sourceRequest is not null
                && !string.IsNullOrEmpty(Response?.Body))
            {
                await UpdateDynamicCacheFromResponseAsync(Response!.Body, ct).ConfigureAwait(false);
                // Do NOT re-resolve the URL preview here — after a send, the environment has not
                // changed, so the preview URL will never need to re-resolve.
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
    // Commands — Save / Revert
    // -------------------------------------------------------------------------

    /// <summary>
    /// Saves the current tab. For existing requests, writes to disk immediately.
    /// For new (unsaved) tabs, opens the Save As panel.
    /// Always enabled so Ctrl+S works on both tab types.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
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
            return;
        }

        await PerformSaveAsync(ct);
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

        var saved = await PerformSaveAsync(ct);

        if (!saved)
            return;

        if (_closeAfterSaveAs)
        {
            _closeAfterSaveAs = false;
            PendingClose = false;
            _requestClose(this);
        }

        // Tell the sidebar to refresh so the new file appears in the tree.
        _messenger.Send(new CollectionRefreshRequestedMessage());
    }

    [RelayCommand]
    private void CancelSaveAs()
    {
        _closeAfterSaveAs = false;
        ShowSaveAsPanel = false;
        SaveAsError = null;
    }

    /// <summary>
    /// Reverts all unsaved changes by reloading the last-saved state from <see cref="_sourceRequest"/>.
    /// Only available for existing (non-new) tabs that have unsaved changes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        if (_sourceRequest is null) return;
        LoadRequest(_sourceRequest);
    }

    private bool CanRevert() => HasUnsavedChanges && !IsNew;

    // -------------------------------------------------------------------------
    // Commands — Close + close guard
    // -------------------------------------------------------------------------

    /// <summary>
    /// Requests closing this tab. Tabs that still need a save action show the inline
    /// close guard before closing.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        if (TabIsDirty)
        {
            if (_requestGlobalCloseGuard is not null)
            {
                _requestGlobalCloseGuard(this);
                return;
            }

            PendingClose = true;
            return;
        }
        _requestClose(this);
    }

    [RelayCommand]
    private async Task SaveAndCloseAsync(CancellationToken ct)
    {
        if (IsNew)
        {
            _closeAfterSaveAs = true;
            PendingClose = false;
            await SaveAsync(ct);
            return;
        }

        var saved = await PerformSaveAsync(ct);
        if (!saved) return;  // save failed — keep the modal open
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

    internal async Task<bool> PerformSaveAsync(CancellationToken ct = default)
    {
        if (_sourceRequest is null) return false;

        // Snapshot the per-type body contents dictionary, making sure the active body
        // type reflects the current editor content.
        var allBodyContents = new Dictionary<string, string>(_bodyContents, StringComparer.Ordinal);
        if (SelectedBodyType is CollectionRequest.BodyTypes.Json
                             or CollectionRequest.BodyTypes.Text
                             or CollectionRequest.BodyTypes.Xml
                             or CollectionRequest.BodyTypes.Yaml
                             or CollectionRequest.BodyTypes.Other
            && !string.IsNullOrEmpty(Body))
        {
            allBodyContents[SelectedBodyType] = Body;
        }

        var updated = new CollectionRequest
        {
            FilePath = _sourceRequest.FilePath,
            Name = RequestName,
            Method = new HttpMethod(SelectedMethod),
            Url = Url,
            RequestId = _sourceRequest.RequestId ?? Guid.NewGuid(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
            Headers = Headers.GetAllKv(),
            PathParams = PathParams.GetEnabledPairs().ToDictionary(p => p.Key, p => p.Value),
            QueryParams = QueryParams.GetAllKv(),
            BodyType = SelectedBodyType,
            Body = string.IsNullOrEmpty(Body) ? null : Body,
            FileBodyBase64 = SelectedBodyType == CollectionRequest.BodyTypes.File && _fileBodyBytes is not null
                ? Convert.ToBase64String(_fileBodyBytes)
                : null,
            FileBodyName = SelectedBodyType == CollectionRequest.BodyTypes.File ? _fileBodyName : null,
            AllBodyContents = allBodyContents,
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
            return true;
        }
        catch (OperationCanceledException)
        {
            // Save was cancelled — leave HasUnsavedChanges as-is so the user can retry.
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Save failed: {ex.Message}";
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    // -------------------------------------------------------------------------
    // Body type switching
    // -------------------------------------------------------------------------

    partial void OnSelectedBodyTypeChanged(string? oldValue, string newValue)
    {
        if (_loading) return;

        // Stash the current editor content under the old body type.
        if (oldValue is CollectionRequest.BodyTypes.Json
                     or CollectionRequest.BodyTypes.Text
                     or CollectionRequest.BodyTypes.Xml
                     or CollectionRequest.BodyTypes.Yaml
                     or CollectionRequest.BodyTypes.Other)
        {
            _bodyContents[oldValue] = Body;
        }

        // Restore the editor content that was last used with the new body type.
        if (newValue is CollectionRequest.BodyTypes.Json
                     or CollectionRequest.BodyTypes.Text
                     or CollectionRequest.BodyTypes.Xml
                     or CollectionRequest.BodyTypes.Yaml
                     or CollectionRequest.BodyTypes.Other)
        {
            Body = _bodyContents.GetValueOrDefault(newValue, string.Empty);
        }
        else
        {
            // Switching to "none", "form", "multipart", or "file" — clear the text editor.
            Body = string.Empty;
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
            Url = QueryStringHelper.AppendQueryParams(updatedBaseUrl, QueryParams.GetEnabledPairs().ToList());
        }
        finally
        {
            _syncingUrl = false;
        }
    }

    // -------------------------------------------------------------------------
    // Preview URL helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the variable dictionary used by <see cref="PreviewUrl"/>.
    /// Calls <see cref="IEnvironmentMergeService.BuildStaticMerge"/> for values then removes
    /// any entry whose "winning" variable definition — determined by the same three-pass
    /// precedence as <c>BuildStaticMerge</c> (non-override globals → active env → force-override
    /// globals) — is a non-static type (ResponseBody, MockData, Dynamic, Script, or Chained).
    /// This means static variables with an empty value are still substituted (resulting in an
    /// empty string in the URL), while dynamic variables leave their <c>{{token}}</c> intact.
    /// </summary>
    private Dictionary<string, string> BuildPreviewVars()
    {
        var merged = _mergeService.BuildStaticMerge(_globalEnvironment, _activeEnvironment);

        // Track the "winning" isDynamic flag for each name using the same three-pass
        // precedence order as BuildStaticMerge.
        var dynamicNames = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var variable in _globalEnvironment.Variables.Where(ev => !ev.IsForceGlobalOverride))
            dynamicNames[variable.Name] = IsNonStaticVariableType(variable.VariableType);

        if (_activeEnvironment is not null)
            foreach (var variable in _activeEnvironment.Variables)
                dynamicNames[variable.Name] = IsNonStaticVariableType(variable.VariableType);

        foreach (var variable in _globalEnvironment.Variables.Where(ev => ev.IsForceGlobalOverride && !string.IsNullOrWhiteSpace(ev.Name)))
            dynamicNames[variable.Name] = IsNonStaticVariableType(variable.VariableType);

        // Remove dynamic vars so their {{tokens}} are left untouched by VariableSubstitutionService.
        foreach (var name in dynamicNames.Where(kv => kv.Value).Select(kv => kv.Key))
            merged.Remove(name);

        return merged;
    }

    private static bool IsNonStaticVariableType(string variableType) =>
        variableType is EnvironmentVariable.VariableTypes.Dynamic
            or EnvironmentVariable.VariableTypes.ResponseBody
            or EnvironmentVariable.VariableTypes.MockData
            or EnvironmentVariable.VariableTypes.Script
            or EnvironmentVariable.VariableTypes.Chained;

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

    /// <summary>
    /// Returns the effective auth configuration for the current request.
    /// If the request's auth is "inherit", walks up the folder hierarchy via the collection service.
    /// </summary>
    private async Task<AuthConfig> GetEffectiveAuthAsync(CancellationToken ct)
    {
        if (AuthType != AuthConfig.AuthTypes.Inherit)
        {
            return new AuthConfig
            {
                AuthType = AuthType,
                Token = string.IsNullOrEmpty(AuthToken) ? null : AuthToken,
                Username = string.IsNullOrEmpty(AuthUsername) ? null : AuthUsername,
                Password = string.IsNullOrEmpty(AuthPassword) ? null : AuthPassword,
                ApiKeyName = string.IsNullOrEmpty(AuthApiKeyName) ? null : AuthApiKeyName,
                ApiKeyValue = string.IsNullOrEmpty(AuthApiKeyValue) ? null : AuthApiKeyValue,
                ApiKeyIn = AuthApiKeyIn,
            };
        }

        if (string.IsNullOrEmpty(SourceFilePath))
            return new AuthConfig { AuthType = AuthConfig.AuthTypes.None };

        return await _collectionService.ResolveEffectiveAuthAsync(SourceFilePath, ct).ConfigureAwait(false)
            ?? new AuthConfig { AuthType = AuthConfig.AuthTypes.None };
    }

    private static void ApplyAuthHeaders(
        Dictionary<string, string> headers,
        string requestUrl,
        IReadOnlyDictionary<string, string> vars,
        out string url,
        AuthConfig auth,
        IReadOnlyDictionary<string, MockDataEntry>? mockGenerators = null,
        IReadOnlySet<string>? secretVariableNames = null,
        IList<VariableBinding>? collector = null)
    {
        url = requestUrl;

        string Resolve(string? template) =>
            collector is not null && secretVariableNames is not null
                ? VariableSubstitutionService.SubstituteCollecting(template, vars, secretVariableNames, collector, mockGenerators) ?? template ?? string.Empty
                : VariableSubstitutionService.Substitute(template, vars) ?? template ?? string.Empty;

        switch (auth.AuthType)
        {
            case AuthConfig.AuthTypes.Bearer when !string.IsNullOrEmpty(auth.Token):
                var token = Resolve(auth.Token);
                headers[WellKnownHeaders.Authorization] = $"Bearer {token}";
                break;
            case AuthConfig.AuthTypes.Basic when !string.IsNullOrEmpty(auth.Username):
                var username = Resolve(auth.Username);
                var password = Resolve(auth.Password);
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{username}:{password}"));
                headers[WellKnownHeaders.Authorization] = $"Basic {encoded}";
                break;
            case AuthConfig.AuthTypes.ApiKey when !string.IsNullOrEmpty(auth.ApiKeyName)
                                               && !string.IsNullOrEmpty(auth.ApiKeyValue):
                var resolvedName = Resolve(auth.ApiKeyName);
                var resolvedValue = Resolve(auth.ApiKeyValue);
                if (string.IsNullOrWhiteSpace(resolvedName))
                    break;

                if (auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header)
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
            var names = IsBrunoCollection
                ? PathTemplateHelper.ExtractPathParamNamesColon(url)
                : PathTemplateHelper.ExtractPathParamNames(url);
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

    private string? GetContentType() => CollectionRequest.BodyTypes.ToContentType(SelectedBodyType);

    // -------------------------------------------------------------------------
    // History helpers
    // -------------------------------------------------------------------------

    private async Task RecordHistoryAsync(
        ResolvedEnvironment env,
        ResponseModel response,
        string resolvedUrl,
        DateTimeOffset sentAt,
        AuthConfig? effectiveAuth = null,
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

            void CollectAuthFields(AuthConfig auth)
            {
                Collect(auth.Token);
                Collect(auth.Username);
                Collect(auth.Password);
                Collect(auth.ApiKeyName);
                Collect(auth.ApiKeyValue);
            }

            Collect(Url);
            foreach (var p in QueryParams.GetAllKv()) { Collect(p.Key); Collect(p.Value); }
            foreach (var p in Headers.GetAllKv())    { Collect(p.Key); Collect(p.Value); }
            Collect(Body);
            foreach (var p in FormParams.GetAllKv())  { Collect(p.Key); Collect(p.Value); }
            foreach (var p in PathParams.GetEnabledPairs()) Collect(p.Value);
            CollectAuthFields(new AuthConfig
            {
                Token = string.IsNullOrEmpty(AuthToken) ? null : AuthToken,
                Username = string.IsNullOrEmpty(AuthUsername) ? null : AuthUsername,
                Password = string.IsNullOrEmpty(AuthPassword) ? null : AuthPassword,
                ApiKeyName = string.IsNullOrEmpty(AuthApiKeyName) ? null : AuthApiKeyName,
                ApiKeyValue = string.IsNullOrEmpty(AuthApiKeyValue) ? null : AuthApiKeyValue,
            });

            // When auth is inherited, the request's own auth fields are not used for sending.
            // Collect variable bindings from the effective (inherited) auth fields so that
            // the Resolved view can correctly substitute any {{tokens}} in the auth config.
            if (AuthType == AuthConfig.AuthTypes.Inherit && effectiveAuth is not null)
                CollectAuthFields(effectiveAuth);

            // Deduplicate — same token may appear in multiple fields.
            var dedupedBindings = bindings
                .GroupBy(b => b.Token)
                .Select(g => g.First())
                .ToList();

            var contentType = GetContentType();
            IReadOnlyList<RequestKv> autoAppliedHeaders = contentType is not null
                ? [new RequestKv(WellKnownHeaders.ContentType, contentType)]
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
                FileBodyBase64 = SelectedBodyType == CollectionRequest.BodyTypes.File && _fileBodyBytes is not null
                    ? Convert.ToBase64String(_fileBodyBytes)
                    : null,
                FileBodyName = SelectedBodyType == CollectionRequest.BodyTypes.File ? _fileBodyName : null,
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
                EffectiveAuth = effectiveAuth,
            };

            var entry = new HistoryEntry
            {
                RequestId = _sourceRequest?.RequestId,
                SentAt = sentAt,
                Method = SelectedMethod,
                StatusCode = response.StatusCode,
                ResolvedUrl = resolvedUrl,
                RequestName = _sourceRequest is not null ? RequestName : null,
                EnvironmentName = _activeEnvironment?.Name,
                EnvironmentId = _activeEnvironment?.EnvironmentId,
                EnvironmentColor = _activeEnvironment?.Color,
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
