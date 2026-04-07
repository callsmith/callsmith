using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Folder Settings dialog.
/// Allows the user to configure folder-level settings, starting with authentication.
/// Auth is persisted to the folder's <c>_meta.json</c> file via <see cref="ICollectionService"/>.
/// </summary>
public sealed partial class FolderSettingsViewModel : ObservableObject
{
    private readonly ICollectionService _collectionService;
    private readonly string _folderPath;
    private readonly string _folderName;

    /// <summary>Raised when the dialog should close (after save or cancel).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Display name of the folder being edited, shown in the dialog title.</summary>
    public string FolderName => _folderName;

    // -------------------------------------------------------------------------
    // Auth properties
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthInherit))]
    [NotifyPropertyChangedFor(nameof(IsAuthBearer))]
    [NotifyPropertyChangedFor(nameof(IsAuthBasic))]
    [NotifyPropertyChangedFor(nameof(IsAuthApiKey))]
    private string _authType = AuthConfig.AuthTypes.Inherit;

    [ObservableProperty] private string _authToken = string.Empty;
    [ObservableProperty] private string _authUsername = string.Empty;
    [ObservableProperty] private string _authPassword = string.Empty;
    [ObservableProperty] private bool _showAuthPassword;
    [ObservableProperty] private string _authApiKeyName = string.Empty;
    [ObservableProperty] private string _authApiKeyValue = string.Empty;
    [ObservableProperty] private bool _showAuthApiKeyValue;
    [ObservableProperty] private string _authApiKeyIn = AuthConfig.ApiKeyLocations.Header;

    /// <summary>Error message shown below the form, or null when there is no error.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Autocomplete suggestions for environment variable names.
    /// Bound to all auth text boxes so the user can type {{ and pick a variable.
    /// </summary>
    public IReadOnlyList<EnvVarSuggestion> EnvVarSuggestions { get; }

    // -------------------------------------------------------------------------
    // Derived visibility
    // -------------------------------------------------------------------------

    public bool IsAuthInherit => AuthType == AuthConfig.AuthTypes.Inherit;
    public bool IsAuthBearer  => AuthType == AuthConfig.AuthTypes.Bearer;
    public bool IsAuthBasic   => AuthType == AuthConfig.AuthTypes.Basic;
    public bool IsAuthApiKey  => AuthType == AuthConfig.AuthTypes.ApiKey;

    /// <summary>Auth type options shown in the ComboBox (inherit is first/default).</summary>
    public IReadOnlyList<string> AuthTypes { get; } =
    [
        AuthConfig.AuthTypes.Inherit,
        AuthConfig.AuthTypes.None,
        AuthConfig.AuthTypes.Bearer,
        AuthConfig.AuthTypes.Basic,
        AuthConfig.AuthTypes.ApiKey,
    ];

    /// <summary>API key location options shown in the ComboBox.</summary>
    public IReadOnlyList<string> ApiKeyLocations { get; } =
    [
        AuthConfig.ApiKeyLocations.Header,
        AuthConfig.ApiKeyLocations.Query,
    ];

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initialises the ViewModel for the given folder node, pre-loading its current auth.
    /// </summary>
    public FolderSettingsViewModel(
        CollectionTreeItemViewModel node,
        ICollectionService collectionService,
        IReadOnlyList<EnvVarSuggestion>? envVarSuggestions = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(collectionService);

        _collectionService = collectionService;
        _folderPath = node.FolderPath ?? string.Empty;
        _folderName = node.Name;

        EnvVarSuggestions = envVarSuggestions ?? [];

        // Pre-load current auth from the folder model.
        var auth = node.FolderAuth ?? new AuthConfig();
        AuthType = auth.AuthType;
        AuthToken = auth.Token ?? string.Empty;
        AuthUsername = auth.Username ?? string.Empty;
        AuthPassword = auth.Password ?? string.Empty;
        AuthApiKeyName = auth.ApiKeyName ?? string.Empty;
        AuthApiKeyValue = auth.ApiKeyValue ?? string.Empty;
        AuthApiKeyIn = auth.ApiKeyIn;
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        ErrorMessage = null;

        if (string.IsNullOrEmpty(_folderPath))
        {
            ErrorMessage = "Cannot save: folder path is not available.";
            return;
        }

        try
        {
            var auth = BuildAuthConfig();
            await _collectionService.SaveFolderAuthAsync(_folderPath, auth, ct);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Silently ignore if cancelled.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private AuthConfig BuildAuthConfig() =>
        new()
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
