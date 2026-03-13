using System.Net.Http;
using System.Text;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Callsmith.Desktop.Messages;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the request editor and response viewer pane.
/// Receives a selected request from the sidebar via <see cref="RequestSelectedMessage"/>,
/// allows the user to edit and send it, and exposes the response for display.
/// Tracks unsaved changes and provides an explicit Save command.
/// </summary>
public sealed partial class RequestViewModel : ObservableRecipient, IRecipient<RequestSelectedMessage>
{
    private readonly TransportRegistry _transportRegistry;
    private readonly ICollectionService _collectionService;

    /// <summary>The request file currently loaded into the editor; null when nothing is open.</summary>
    private CollectionRequest? _sourceRequest;

    /// <summary>Suppresses dirty-tracking while populating the editor from a loaded request.</summary>
    private bool _loading;

    /// <summary>Prevents infinite feedback between the URL bar and the query-param editor.</summary>
    private bool _syncingUrl;

    // -------------------------------------------------------------------------
    // Request editor state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private string _selectedMethod = "GET";

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
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

    [ObservableProperty]
    private string _authToken = string.Empty;

    [ObservableProperty]
    private string _authUsername = string.Empty;

    [ObservableProperty]
    private string _authPassword = string.Empty;

    [ObservableProperty]
    private string _authApiKeyName = string.Empty;

    [ObservableProperty]
    private string _authApiKeyValue = string.Empty;

    [ObservableProperty]
    private string _authApiKeyIn = AuthConfig.ApiKeyLocations.Header;

    // -------------------------------------------------------------------------
    // Save state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _hasUnsavedChanges;

    // -------------------------------------------------------------------------
    // Response state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private ResponseModel? _response;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private string? _errorMessage;

    // -------------------------------------------------------------------------
    // Key-value editors
    // -------------------------------------------------------------------------

    /// <summary>Request headers editor.</summary>
    public KeyValueEditorViewModel Headers { get; } = new();

    /// <summary>Query parameter editor -- stays bidirectionally in sync with the URL bar.</summary>
    public KeyValueEditorViewModel QueryParams { get; } = new();

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

    public string StatusClass => Response?.StatusCode switch
    {
        >= 200 and < 300 => "success",
        >= 300 and < 400 => "redirect",
        >= 400 and < 500 => "client-error",
        >= 500 => "server-error",
        _ => string.Empty,
    };

    /// <summary>Whether a request file is currently loaded into the editor.</summary>
    public bool HasSourceRequest => _sourceRequest is not null;

    /// <summary>Whether the body text editor should be visible (body type is not "none").</summary>
    public bool ShowBodyEditor => SelectedBodyType != CollectionRequest.BodyTypes.None;

    public bool IsAuthBearer => AuthType == AuthConfig.AuthTypes.Bearer;
    public bool IsAuthBasic => AuthType == AuthConfig.AuthTypes.Basic;
    public bool IsAuthApiKey => AuthType == AuthConfig.AuthTypes.ApiKey;

    // -------------------------------------------------------------------------
    // Lists for ComboBox controls
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

    public RequestViewModel(
        TransportRegistry transportRegistry,
        ICollectionService collectionService,
        IMessenger messenger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(transportRegistry);
        ArgumentNullException.ThrowIfNull(collectionService);
        _transportRegistry = transportRegistry;
        _collectionService = collectionService;
        IsActive = true;

        // Dirty tracking: any property change while not loading marks the editor dirty.
        // Exclude internal/computed properties that are not user edits.
        PropertyChanged += (_, e) =>
        {
            if (_loading || _sourceRequest is null) return;
            if (e.PropertyName is
                nameof(HasUnsavedChanges) or nameof(HasSourceRequest) or
                nameof(Response) or nameof(IsSending) or nameof(ErrorMessage) or
                nameof(StatusDisplay) or nameof(ElapsedDisplay) or nameof(SizeDisplay) or nameof(StatusClass) or
                nameof(ShowBodyEditor) or
                nameof(IsAuthBearer) or nameof(IsAuthBasic) or nameof(IsAuthApiKey))
                return;
            HasUnsavedChanges = true;
        };

        Headers.Changed += (_, _) =>
        {
            if (!_loading && _sourceRequest is not null)
                HasUnsavedChanges = true;
        };

        QueryParams.Changed += (_, _) =>
        {
            if (!_loading && _sourceRequest is not null)
                HasUnsavedChanges = true;
            RebuildUrlFromParams();
        };
    }

    // -------------------------------------------------------------------------
    // Messenger receiver
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called when the user selects a request in the collections sidebar.
    /// Populates all editor fields and clears the unsaved-changes flag.
    /// </summary>
    public void Receive(RequestSelectedMessage message)
    {
        _sourceRequest = message.Value;
        var req = message.Value;

        _loading = true;
        try
        {
            RequestName = req.Name;
            SelectedMethod = req.Method.Method;

            // Build the display URL from base URL + stored params, then populate both
            // the URL bar and the params editor without triggering the sync loop.
            _syncingUrl = true;
            try
            {
                Url = req.QueryParams.Count > 0
                    ? QueryStringHelper.ApplyQueryParams(req.Url, req.QueryParams)
                    : req.Url;
                QueryParams.LoadFrom(req.QueryParams);
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
        }

        OnPropertyChanged(nameof(HasSourceRequest));
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    /// <summary>Sends the current request and populates the response viewer.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Url))
            return;

        IsSending = true;
        Response = null;
        ErrorMessage = null;

        try
        {
            var headers = new Dictionary<string, string>(
                Headers.GetEnabledPairs().ToDictionary(p => p.Key, p => p.Value),
                StringComparer.OrdinalIgnoreCase);

            ApplyAuthHeaders(headers, out var requestUrl);

            var request = new RequestModel
            {
                Method = new HttpMethod(SelectedMethod),
                Url = requestUrl,
                Headers = headers,
                Body = SelectedBodyType != CollectionRequest.BodyTypes.None && !string.IsNullOrEmpty(Body)
                    ? Body
                    : null,
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
            OnPropertyChanged(nameof(StatusClass));
        }
    }

    /// <summary>Saves the current editor state back to disk, clearing the unsaved-changes flag.</summary>
    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (_sourceRequest is null) return;

        // Store base URL (no query string) and params separately for a clean file format.
        var baseUrl = Url.Contains('?') ? Url[..Url.IndexOf('?')] : Url;

        var updated = new CollectionRequest
        {
            FilePath = _sourceRequest.FilePath,
            Name = RequestName,
            Method = new HttpMethod(SelectedMethod),
            Url = baseUrl,
            Description = _sourceRequest.Description,
            Headers = Headers.GetEnabledPairs().ToDictionary(p => p.Key, p => p.Value),
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

        await _collectionService.SaveRequestAsync(updated, ct);
        _sourceRequest = updated;
        HasUnsavedChanges = false;
    }

    // -------------------------------------------------------------------------
    // URL <-> query param sync
    // -------------------------------------------------------------------------

    partial void OnUrlChanged(string value)
    {
        if (_syncingUrl || _loading) return;
        _syncingUrl = true;
        try
        {
            var parsed = QueryStringHelper.ParseQueryParams(value);
            QueryParams.LoadFrom(parsed);
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

    // -------------------------------------------------------------------------
    // Auth helpers
    // -------------------------------------------------------------------------

    private void ApplyAuthHeaders(Dictionary<string, string> headers, out string url)
    {
        url = Url;
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
                        Url,
                        [new KeyValuePair<string, string>(AuthApiKeyName, AuthApiKeyValue)]);
                break;
        }
    }

    private string? GetContentType() => SelectedBodyType switch
    {
        CollectionRequest.BodyTypes.Json => "application/json",
        CollectionRequest.BodyTypes.Text => "text/plain",
        CollectionRequest.BodyTypes.Xml => "application/xml",
        CollectionRequest.BodyTypes.Form => "application/x-www-form-urlencoded",
        CollectionRequest.BodyTypes.Multipart => "multipart/form-data",
        _ => null,
    };
}