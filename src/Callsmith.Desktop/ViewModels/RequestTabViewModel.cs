using System.Net.Http;
using System.Text;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Callsmith.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Callsmith.Desktop.Messages;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// State for a single open request tab. Each tab is an independent editor instance.
/// <see cref="RequestEditorViewModel"/> manages the collection of open tabs and
/// handles cross-tab concerns (environment changes, collection events).
/// </summary>
public sealed partial class RequestTabViewModel : ObservableObject
{
    private readonly TransportRegistry _transportRegistry;
    private readonly ICollectionService _collectionService;
    private readonly IMessenger _messenger;
    private readonly Action<RequestTabViewModel> _requestClose;

    /// <summary>Source request loaded from disk. Null for brand-new unsaved tabs.</summary>
    private CollectionRequest? _sourceRequest;

    private EnvironmentModel? _activeEnvironment;
    private bool _loading;
    private bool _syncingUrl;
    private bool _syncingPathParams;

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
    private string _selectedMethod = "GET";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    [NotifyPropertyChangedFor(nameof(PreviewUrl))]
    [NotifyPropertyChangedFor(nameof(HasUnresolvedPathParams))]
    [NotifyPropertyChangedFor(nameof(PreviewUrlForeground))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private string _requestName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBodyEditor))]
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
    [ObservableProperty] private string _authApiKeyName = string.Empty;
    [ObservableProperty] private string _authApiKeyValue = string.Empty;
    [ObservableProperty] private string _authApiKeyIn = AuthConfig.ApiKeyLocations.Header;

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

    // -------------------------------------------------------------------------
    // Close guard (shown when closing a dirty existing tab)
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private bool _pendingClose;

    // -------------------------------------------------------------------------
    // Response state
    // -------------------------------------------------------------------------

    [ObservableProperty] private ResponseModel? _response;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string? _errorMessage;

    // -------------------------------------------------------------------------
    // Key-value editors
    // -------------------------------------------------------------------------

    public KeyValueEditorViewModel Headers { get; } = new();
    public KeyValueEditorViewModel QueryParams { get; } = new();
    public KeyValueEditorViewModel PathParams { get; } = new();

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

    public string MethodColor => SelectedMethod switch
    {
        "GET"     => "#4ec9b0",
        "POST"    => "#dda756",
        "PUT"     => "#4fc1ff",
        "PATCH"   => "#b8d7a3",
        "DELETE"  => "#f48771",
        "HEAD"    => "#c586c0",
        "OPTIONS" => "#9a9a9a",
        _         => "#d4d4d4",
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

            var pathParamValues = PathParams.GetEnabledPairs()
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .ToDictionary(p => p.Key, p => p.Value);

            var baseUrl = GetBaseUrl(Url);
            var resolved = PathTemplateHelper.ApplyPathParams(baseUrl, pathParamValues);
            resolved = QueryStringHelper.ApplyQueryParams(resolved, QueryParams.GetEnabledPairs().ToList());

            var vars = _activeEnvironment is not null
                ? (IReadOnlyDictionary<string, string>)_activeEnvironment.Variables
                    .ToDictionary(v => v.Name, v => v.Value)
                : new Dictionary<string, string>();

            return VariableSubstitutionService.Substitute(resolved, vars) ?? resolved;
        }
    }

    public bool ShowBodyEditor => SelectedBodyType != CollectionRequest.BodyTypes.None;
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
        TransportRegistry transportRegistry,
        ICollectionService collectionService,
        IMessenger messenger,
        Action<RequestTabViewModel> requestClose)
    {
        ArgumentNullException.ThrowIfNull(transportRegistry);
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(messenger);
        ArgumentNullException.ThrowIfNull(requestClose);

        _transportRegistry = transportRegistry;
        _collectionService = collectionService;
        _messenger = messenger;
        _requestClose = requestClose;

        PathParams.ShowAddButton = false;
        PathParams.ShowDeleteButton = false;
        PathParams.ShowEnabledToggle = false;

        // Dirty tracking — only fires for changes to existing requests.
        PropertyChanged += (_, e) =>
        {
            if (_loading || _sourceRequest is null) return;
            if (e.PropertyName is
                nameof(HasUnsavedChanges) or nameof(IsNew) or nameof(IsActive) or nameof(TabTitle) or
                nameof(TabIsDirty) or nameof(SaveButtonLabel) or
                nameof(ShowSaveAsPanel) or nameof(SaveAsName) or nameof(SaveAsFolderPath) or
                nameof(SaveAsError) or nameof(PendingClose) or
                nameof(Response) or nameof(IsSending) or nameof(ErrorMessage) or
                nameof(StatusDisplay) or nameof(ElapsedDisplay) or nameof(SizeDisplay) or
                nameof(StatusBadgeColor) or nameof(MethodColor) or
                nameof(ShowBodyEditor) or nameof(PreviewUrl) or
                nameof(HasUnresolvedPathParams) or nameof(PreviewUrlForeground) or
                nameof(IsAuthBearer) or nameof(IsAuthBasic) or nameof(IsAuthApiKey))
                return;
            HasUnsavedChanges = true;
        };

        Headers.Changed += (_, _) =>
        {
            if (!_loading && _sourceRequest is not null) HasUnsavedChanges = true;
        };

        QueryParams.Changed += (_, _) =>
        {
            if (!_loading && _sourceRequest is not null) HasUnsavedChanges = true;
            RebuildUrlFromParams();
            OnPropertyChanged(nameof(PreviewUrl));
        };

        PathParams.Changed += (_, _) =>
        {
            if (!_loading && _sourceRequest is not null) HasUnsavedChanges = true;
            RebuildUrlFromPathParamNames();
            OnPropertyChanged(nameof(PreviewUrl));
            OnPropertyChanged(nameof(HasUnresolvedPathParams));
            OnPropertyChanged(nameof(PreviewUrlForeground));
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
            SelectedMethod = req.Method.Method;

            _syncingUrl = true;
            try
            {
                Url = req.QueryParams.Count > 0
                    ? QueryStringHelper.ApplyQueryParams(req.Url, req.QueryParams)
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
            AuthType = req.Auth.AuthType;
            AuthToken = req.Auth.Token ?? string.Empty;
            AuthUsername = req.Auth.Username ?? string.Empty;
            AuthPassword = req.Auth.Password ?? string.Empty;
            AuthApiKeyName = req.Auth.ApiKeyName ?? string.Empty;
            AuthApiKeyValue = req.Auth.ApiKeyValue ?? string.Empty;
            AuthApiKeyIn = req.Auth.ApiKeyIn;
            Response = null;
            ErrorMessage = null;
        }
        finally
        {
            _loading = false;
            HasUnsavedChanges = false;
            IsNew = false;
        }
    }

    /// <summary>Updates the active environment for variable substitution.</summary>
    public void SetEnvironment(EnvironmentModel? environment)
    {
        _activeEnvironment = environment;
        OnPropertyChanged(nameof(PreviewUrl));
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
            var headers = new Dictionary<string, string>(
                Headers.GetEnabledPairs().ToDictionary(p => p.Key, p => p.Value),
                StringComparer.OrdinalIgnoreCase);

            var pathParamValues = PathParams.GetEnabledPairs()
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .ToDictionary(p => p.Key, p => p.Value);

            var baseUrl = GetBaseUrl(Url);
            var requestUrl = PathTemplateHelper.ApplyPathParams(baseUrl, pathParamValues);
            requestUrl = QueryStringHelper.ApplyQueryParams(requestUrl, QueryParams.GetEnabledPairs().ToList());

            ApplyAuthHeaders(headers, requestUrl, out requestUrl);

            var vars = _activeEnvironment is not null
                ? (IReadOnlyDictionary<string, string>)_activeEnvironment.Variables
                    .ToDictionary(v => v.Name, v => v.Value)
                : new Dictionary<string, string>();

            requestUrl = VariableSubstitutionService.Substitute(requestUrl, vars) ?? requestUrl;
            foreach (var key in headers.Keys.ToList())
                headers[key] = VariableSubstitutionService.Substitute(headers[key], vars) ?? headers[key];

            var resolvedBody = SelectedBodyType != CollectionRequest.BodyTypes.None && !string.IsNullOrEmpty(Body)
                ? VariableSubstitutionService.Substitute(Body, vars) ?? Body
                : null;

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
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(ElapsedDisplay));
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(StatusBadgeColor));
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
        var filePath = Path.Combine(absoluteFolder, name + Core.Services.FileSystemCollectionService.RequestFileExtension);
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
            Url = string.Empty, // PerformSaveAsync will overwrite from editor state
            Headers = new Dictionary<string, string>(),
            PathParams = new Dictionary<string, string>(),
            QueryParams = new Dictionary<string, string>(),
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
            Description = _sourceRequest.Description,
            Headers = Headers.GetEnabledPairs().ToDictionary(p => p.Key, p => p.Value),
            PathParams = PathParams.GetEnabledPairs().ToDictionary(p => p.Key, p => p.Value),
            QueryParams = QueryParams.GetEnabledPairs().ToDictionary(p => p.Key, p => p.Value),
            BodyType = SelectedBodyType,
            Body = string.IsNullOrEmpty(Body) ? null : Body,
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

        try
        {
            await _collectionService.SaveRequestAsync(updated, ct);
            _sourceRequest = updated;
            HasUnsavedChanges = false;
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = $"Save failed: {ex.Message}";
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

    private void ApplyAuthHeaders(Dictionary<string, string> headers, string requestUrl, out string url)
    {
        url = requestUrl;
        switch (AuthType)
        {
            case AuthConfig.AuthTypes.Bearer when !string.IsNullOrEmpty(AuthToken):
                headers["Authorization"] = $"Bearer {AuthToken}";
                break;
            case AuthConfig.AuthTypes.Basic:
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{AuthUsername}:{AuthPassword}"));
                headers["Authorization"] = $"Basic {encoded}";
                break;
            case AuthConfig.AuthTypes.ApiKey when !string.IsNullOrEmpty(AuthApiKeyName):
                if (AuthApiKeyIn == AuthConfig.ApiKeyLocations.Header)
                    headers[AuthApiKeyName] = AuthApiKeyValue;
                else
                    url = QueryStringHelper.ApplyQueryParams(
                        requestUrl,
                        [new KeyValuePair<string, string>(AuthApiKeyName, AuthApiKeyValue)]);
                break;
        }
    }

    private static string GetBaseUrl(string value)
    {
        var index = value.IndexOf('?');
        return index >= 0 ? value[..index] : value;
    }

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

    private string? GetContentType() => SelectedBodyType switch
    {
        CollectionRequest.BodyTypes.Json => "application/json",
        CollectionRequest.BodyTypes.Text => "text/plain",
        CollectionRequest.BodyTypes.Xml  => "application/xml",
        CollectionRequest.BodyTypes.Form => "application/x-www-form-urlencoded",
        CollectionRequest.BodyTypes.Multipart => "multipart/form-data",
        _ => null,
    };
}
