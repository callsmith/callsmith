using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="ICollectionService"/> implementation that reads and writes requests
/// as <c>.callsmith</c> JSON files on the local filesystem.
/// No database is involved — the filesystem IS the data store.
/// </summary>
public sealed class FileSystemCollectionService : ICollectionService
{
    /// <summary>File extension used for all request files.</summary>
    public const string RequestFileExtension = ".callsmith";

    // Exposed via ICollectionService so consumers don't need to reference the concrete type.
    string ICollectionService.RequestFileExtension => RequestFileExtension;

    /// <summary>
    /// Reserved sub-folder name inside a collection folder that holds environment files.
    /// This folder is excluded from request discovery.
    /// </summary>
    public const string EnvironmentFolderName = "environment";

    /// <summary>File name of the optional display-order manifest written in each folder.</summary>
    public const string OrderFileName = "_order.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reserved namespace used inside <see cref="ISecretStorageService"/> for Basic auth passwords.</summary>
    private const string AuthSecretsNamespace = "__auth__";

    private readonly ISecretStorageService _secrets;
    private readonly ILogger<FileSystemCollectionService> _logger;

    // Populated by OpenFolderAsync; used to key auth secrets per-collection.
    private string _currentRoot = string.Empty;

    /// <summary>Initialises the service with the provided dependencies.</summary>
    public FileSystemCollectionService(
        ISecretStorageService secrets,
        ILogger<FileSystemCollectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = secrets;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CollectionFolder> OpenFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Collection folder not found: '{folderPath}'");

        ct.ThrowIfCancellationRequested();

        _currentRoot = Path.GetFullPath(folderPath);
        var folder = await Task.Run(() => ReadFolder(folderPath), ct);
        _logger.LogDebug("Opened collection at '{FolderPath}'", folderPath);

        return folder;
    }

    /// <inheritdoc/>
    public async Task<CollectionRequest> LoadRequestAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Request file not found: '{filePath}'", filePath);

        var json = await File.ReadAllTextAsync(filePath, ct);
        var dto = JsonSerializer.Deserialize<RequestFileDto>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialise request file: '{filePath}'");

        _logger.LogDebug("Loaded request from '{FilePath}'", filePath);

        string? basicAuthPassword = null;
        if ((dto.AuthType ?? AuthConfig.AuthTypes.None) == AuthConfig.AuthTypes.Basic
            && !string.IsNullOrEmpty(_currentRoot))
        {
            // Prefer the secret-stored value; fall back to whatever is in the file (migration path).
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, AuthSecretsNamespace, RequestKey(filePath), ct)
                .ConfigureAwait(false);
            basicAuthPassword = stored ?? dto.AuthPassword;
        }

        return DtoToRequest(dto, filePath, basicAuthPassword);
    }

    /// <inheritdoc/>
    public async Task SaveRequestAsync(CollectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var directory = Path.GetDirectoryName(request.FilePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path '{request.FilePath}'");

        Directory.CreateDirectory(directory);

        // Persist Basic auth password locally so it is never written into the collection file.
        if (request.Auth.AuthType == AuthConfig.AuthTypes.Basic && !string.IsNullOrEmpty(_currentRoot))
        {
            await _secrets
                .SetSecretAsync(
                    _currentRoot,
                    AuthSecretsNamespace,
                    RequestKey(request.FilePath),
                    request.Auth.Password ?? string.Empty,
                    ct)
                .ConfigureAwait(false);
        }

        var dto = RequestToDto(request);
        var json = JsonSerializer.Serialize(dto, JsonOptions);

        await File.WriteAllTextAsync(request.FilePath, json, ct);

        _logger.LogDebug("Saved request to '{FilePath}'", request.FilePath);
    }

    /// <inheritdoc/>
    public Task DeleteRequestAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Request file not found: '{filePath}'", filePath);

        File.Delete(filePath);
        _logger.LogDebug("Deleted request file '{FilePath}'", filePath);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<CollectionRequest> RenameRequestAsync(
        string filePath, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(newName);

        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New name must not be empty or whitespace.", nameof(newName));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Request file not found: '{filePath}'", filePath);

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path '{filePath}'");

        var newFilePath = Path.Combine(directory, newName + RequestFileExtension);

        if (File.Exists(newFilePath))
            throw new InvalidOperationException($"A request named '{newName}' already exists in this folder.");

        // Load → rename file → save with updated name
        var existing = await LoadRequestAsync(filePath, ct);
        File.Move(filePath, newFilePath);

        var renamed = new CollectionRequest
        {
            FilePath = newFilePath,
            Name = newName,
            Method = existing.Method,
            Url = existing.Url,
            Description = existing.Description,
            Headers = existing.Headers,
            PathParams = existing.PathParams,
            QueryParams = existing.QueryParams,
            BodyType = existing.BodyType,
            Body = existing.Body,
            FormParams = existing.FormParams,
            Auth = existing.Auth,
        };

        _logger.LogDebug("Renamed request '{OldPath}' → '{NewPath}'", filePath, newFilePath);

        return renamed;
    }

    /// <inheritdoc/>
    public async Task<CollectionRequest> MoveRequestAsync(
        string filePath,
        string destinationFolderPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(destinationFolderPath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Request file not found: '{filePath}'", filePath);

        var request = await LoadRequestAsync(filePath, ct).ConfigureAwait(false);

        var destinationDirectory = destinationFolderPath;
        Directory.CreateDirectory(destinationDirectory);

        var fileName = Path.GetFileName(filePath);
        var destinationFilePath = Path.Combine(destinationDirectory, fileName);

        if (File.Exists(destinationFilePath))
            throw new InvalidOperationException($"A request named '{fileName}' already exists in destination folder.");

        ct.ThrowIfCancellationRequested();
        File.Move(filePath, destinationFilePath);

        var movedRequest = new CollectionRequest
        {
            FilePath = destinationFilePath,
            Name = request.Name,
            Method = request.Method,
            Url = request.Url,
            Description = request.Description,
            Headers = request.Headers,
            PathParams = request.PathParams,
            QueryParams = request.QueryParams,
            BodyType = request.BodyType,
            Body = request.Body,
            FormParams = request.FormParams,
            Auth = request.Auth,
        };

        _logger.LogDebug("Moved request '{OldPath}' → '{NewPath}'", filePath, destinationFilePath);

        return movedRequest;
    }

    /// <inheritdoc/>
    public async Task<CollectionRequest> CreateRequestAsync(
        string folderPath, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(name);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        Directory.CreateDirectory(folderPath);

        var filePath = Path.Combine(folderPath, name + RequestFileExtension);
        if (File.Exists(filePath))
            throw new InvalidOperationException($"A request named '{name}' already exists in this folder.");

        var newRequest = new CollectionRequest
        {
            FilePath = filePath,
            Name = name,
            Method = System.Net.Http.HttpMethod.Get,
            Url = string.Empty,
            Headers = [],
            PathParams = new Dictionary<string, string>(),
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            Auth = new AuthConfig(),
        };

        await SaveRequestAsync(newRequest, ct);
        _logger.LogDebug("Created request '{FilePath}'", filePath);
        return newRequest;
    }

    /// <inheritdoc/>
    public Task<CollectionFolder> CreateFolderAsync(
        string parentPath, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parentPath);
        ArgumentNullException.ThrowIfNull(name);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        var newPath = Path.Combine(parentPath, name);
        if (Directory.Exists(newPath))
            throw new InvalidOperationException($"A folder named '{name}' already exists.");

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(newPath);
        _logger.LogDebug("Created folder '{FolderPath}'", newPath);

        return Task.FromResult(new CollectionFolder
        {
            FolderPath = newPath,
            Name = name,
            Requests = [],
            SubFolders = [],
        });
    }

    /// <inheritdoc/>
    public Task<CollectionFolder> RenameFolderAsync(
        string folderPath, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(newName);

        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New name must not be empty.", nameof(newName));

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: '{folderPath}'");

        var parent = Path.GetDirectoryName(folderPath)
            ?? throw new InvalidOperationException("Cannot determine parent directory.");

        var newPath = Path.Combine(parent, newName);
        if (Directory.Exists(newPath))
            throw new InvalidOperationException($"A folder named '{newName}' already exists.");

        ct.ThrowIfCancellationRequested();
        Directory.Move(folderPath, newPath);
        _logger.LogDebug("Renamed folder '{OldPath}' → '{NewPath}'", folderPath, newPath);

        return Task.FromResult(new CollectionFolder
        {
            FolderPath = newPath,
            Name = newName,
            Requests = [],
            SubFolders = [],
        });
    }

    /// <inheritdoc/>
    public Task DeleteFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: '{folderPath}'");

        ct.ThrowIfCancellationRequested();
        FileSystemHelper.DeleteDirectoryRobust(folderPath);
        _logger.LogDebug("Deleted folder '{FolderPath}'", folderPath);

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Private — folder traversal
    // -------------------------------------------------------------------------

    private CollectionFolder ReadFolder(string folderPath)
    {
        var itemOrder = ReadOrderFile(Path.Combine(folderPath, OrderFileName));

        var requests = Directory
            .EnumerateFiles(folderPath, $"*{RequestFileExtension}", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(filePath =>
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var dto = JsonSerializer.Deserialize<RequestFileDto>(json, JsonOptions);
                    return dto is null ? null : DtoToRequest(dto, filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping unreadable request file '{FilePath}'", filePath);
                    return null;
                }
            })
            .OfType<CollectionRequest>()
            .ToList();

        var subFolders = Directory
            .EnumerateDirectories(folderPath)
            .Where(d => !string.Equals(
                Path.GetFileName(d), EnvironmentFolderName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(ReadFolder)
            .ToList();

        return new CollectionFolder
        {
            FolderPath = folderPath,
            Name = Path.GetFileName(folderPath),
            Requests = requests,
            SubFolders = subFolders,
            ItemOrder = itemOrder,
        };
    }

    private static IReadOnlyList<string> ReadOrderFile(string orderFilePath)
    {
        if (!File.Exists(orderFilePath))
            return [];
        try
        {
            var json = File.ReadAllText(orderFilePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task SaveFolderOrderAsync(
        string folderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(orderedNames);

        var orderFilePath = Path.Combine(folderPath, OrderFileName);

        if (orderedNames.Count == 0)
        {
            if (File.Exists(orderFilePath))
            {
                File.Delete(orderFilePath);
                _logger.LogDebug("Removed order file for '{FolderPath}'", folderPath);
            }
            return;
        }

        var json = JsonSerializer.Serialize(orderedNames, JsonOptions);
        await File.WriteAllTextAsync(orderFilePath, json, ct);
        _logger.LogDebug("Saved folder order for '{FolderPath}'", folderPath);
    }

    // -------------------------------------------------------------------------
    // Private — mapping between domain model and on-disk DTO
    // -------------------------------------------------------------------------

    private static CollectionRequest DtoToRequest(RequestFileDto dto, string filePath, string? basicAuthPassword = null)
    {
        // Separate base URL from query params.
        // New files store them separately; old files have the full URL in the url field.
        var rawUrl = dto.Url ?? string.Empty;
        string baseUrl;
        IReadOnlyList<RequestKv> queryParams;

        if (dto.QueryParamEntries is not null)
        {
            // Current format: ordered list preserving duplicates and enabled state.
            baseUrl = rawUrl;
            queryParams = dto.QueryParamEntries
                .Select(e => new RequestKv(e.Name, e.Value, e.Enabled))
                .ToList();
        }
        else if (dto.QueryParams is not null)
        {
            // Legacy format v2: separate dictionary field, all entries enabled.
            baseUrl = rawUrl;
            queryParams = dto.QueryParams
                .Select(kv => new RequestKv(kv.Key, kv.Value))
                .ToList();
        }
        else
        {
            // Legacy format v1: derive query params by parsing the URL field, all enabled.
            baseUrl = rawUrl.Contains('?') ? rawUrl[..rawUrl.IndexOf('?')] : rawUrl;
            queryParams = QueryStringHelper.ParseQueryParams(rawUrl)
                .Select(kv => new RequestKv(kv.Key, kv.Value))
                .ToList();
        }

        // Headers: current format uses HeaderEntries (with enabled state); legacy uses Headers dict.
        IReadOnlyList<RequestKv> headers;
        if (dto.HeaderEntries is not null)
            headers = dto.HeaderEntries.Select(e => new RequestKv(e.Name, e.Value, e.Enabled)).ToList();
        else if (dto.Headers is not null)
            headers = dto.Headers.Select(kv => new RequestKv(kv.Key, kv.Value)).ToList();
        else
            headers = [];

        var formParams = dto.FormParamEntries is not null
            ? dto.FormParamEntries
                .Select(e => new KeyValuePair<string, string>(e.Name, e.Value))
                .ToList()
            : (IReadOnlyList<KeyValuePair<string, string>>)[];

        return new CollectionRequest
        {
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath),
            Method = new HttpMethod(dto.Method ?? HttpMethod.Get.Method),
            Url = baseUrl,
            Description = dto.Description,
            Headers = headers,
            PathParams = dto.PathParams ?? new Dictionary<string, string>(),
            QueryParams = queryParams,
            BodyType = dto.BodyType ?? CollectionRequest.BodyTypes.None,
            Body = dto.Body,
            FormParams = formParams,
            Auth = new AuthConfig
            {
                AuthType = dto.AuthType ?? AuthConfig.AuthTypes.None,
                Token = dto.AuthToken,
                Username = dto.AuthUsername,
                Password = basicAuthPassword ?? dto.AuthPassword,
                ApiKeyName = dto.AuthApiKeyName,
                ApiKeyValue = dto.AuthApiKeyValue,
                ApiKeyIn = dto.AuthApiKeyIn ?? AuthConfig.ApiKeyLocations.Header,
            },
        };
    }

    /// <summary>
    /// Returns a stable, normalised key for a request file relative to the current collection root.
    /// Used to key Basic auth passwords in local secret storage.
    /// </summary>
    private string RequestKey(string filePath) =>
        Path.GetRelativePath(_currentRoot, Path.GetFullPath(filePath)).Replace('\\', '/');

    private static RequestFileDto RequestToDto(CollectionRequest request) =>
        new()
        {
            Method = request.Method.Method,
            Url = request.Url,
            Description = request.Description,
            HeaderEntries = request.Headers.Count > 0
                ? request.Headers
                    .Select(h => new HeaderEntryDto { Name = h.Key, Value = h.Value, Enabled = h.IsEnabled })
                    .ToList()
                : null,
            PathParams = request.PathParams.Count > 0
                ? request.PathParams.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                : null,
            QueryParamEntries = request.QueryParams.Count > 0
                ? request.QueryParams
                    .Select(p => new QueryParamEntryDto { Name = p.Key, Value = p.Value, Enabled = p.IsEnabled })
                    .ToList()
                : null,
            FormParamEntries = request.FormParams.Count > 0
                ? request.FormParams
                    .Select(kvp => new QueryParamEntryDto { Name = kvp.Key, Value = kvp.Value })
                    .ToList()
                : null,
            BodyType = request.BodyType == CollectionRequest.BodyTypes.None
                ? null
                : request.BodyType,
            Body = request.Body,
            AuthType = request.Auth.AuthType == AuthConfig.AuthTypes.None
                ? null
                : request.Auth.AuthType,
            AuthToken = request.Auth.Token,
            AuthUsername = request.Auth.Username,
            // Basic auth passwords are stored in local secret storage, never in the file.
            AuthPassword = request.Auth.AuthType == AuthConfig.AuthTypes.Basic
                ? null
                : request.Auth.Password,
            AuthApiKeyName = request.Auth.ApiKeyName,
            AuthApiKeyValue = request.Auth.ApiKeyValue,
            AuthApiKeyIn = request.Auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header
                ? null
                : request.Auth.ApiKeyIn,
        };

    // -------------------------------------------------------------------------
    // Private — on-disk JSON DTO
    // -------------------------------------------------------------------------

    /// <summary>
    /// The exact JSON structure written to and read from each <c>.callsmith</c> file.
    /// Kept private — only <see cref="FileSystemCollectionService"/> knows about it.
    /// </summary>
    private sealed class RequestFileDto
    {
        public string? Method { get; set; }
        public string? Url { get; set; }
        public string? Description { get; set; }
        /// <summary>Current format: ordered list with enabled state.</summary>
        public List<HeaderEntryDto>? HeaderEntries { get; set; }
        /// <summary>Legacy format (v2): read-only for backwards compatibility — no enabled state.</summary>
        public Dictionary<string, string>? Headers { get; set; }
        public Dictionary<string, string>? PathParams { get; set; }
        /// <summary>Current format: ordered list preserving duplicate keys and enabled state.</summary>
        public List<QueryParamEntryDto>? QueryParamEntries { get; set; }
        /// <summary>Legacy format (v2): read-only for backwards compatibility.</summary>
        public Dictionary<string, string>? QueryParams { get; set; }
        public List<QueryParamEntryDto>? FormParamEntries { get; set; }
        public string? BodyType { get; set; }
        public string? Body { get; set; }
        public string? AuthType { get; set; }
        public string? AuthToken { get; set; }
        public string? AuthUsername { get; set; }
        public string? AuthPassword { get; set; }
        public string? AuthApiKeyName { get; set; }
        public string? AuthApiKeyValue { get; set; }
        public string? AuthApiKeyIn { get; set; }
    }

    private sealed class HeaderEntryDto
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    private sealed class QueryParamEntryDto
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        /// <summary>Defaults to true for backwards compatibility with files that lack this field.</summary>
        public bool Enabled { get; set; } = true;
    }
}
