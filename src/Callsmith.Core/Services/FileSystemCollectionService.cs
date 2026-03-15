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

    private readonly ILogger<FileSystemCollectionService> _logger;

    /// <summary>Initialises the service with the provided logger.</summary>
    public FileSystemCollectionService(ILogger<FileSystemCollectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CollectionFolder> OpenFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Collection folder not found: '{folderPath}'");

        ct.ThrowIfCancellationRequested();

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

        return DtoToRequest(dto, filePath);
    }

    /// <inheritdoc/>
    public async Task SaveRequestAsync(CollectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var directory = Path.GetDirectoryName(request.FilePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path '{request.FilePath}'");

        Directory.CreateDirectory(directory);

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
            Auth = existing.Auth,
        };

        _logger.LogDebug("Renamed request '{OldPath}' → '{NewPath}'", filePath, newFilePath);

        return renamed;
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
            Headers = new Dictionary<string, string>(),
            PathParams = new Dictionary<string, string>(),
            QueryParams = new Dictionary<string, string>(),
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
        Directory.Delete(folderPath, recursive: true);
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

    private static CollectionRequest DtoToRequest(RequestFileDto dto, string filePath)
    {
        // Separate base URL from query params.
        // New files store them separately; old files have the full URL in the url field.
        var rawUrl = dto.Url ?? string.Empty;
        string baseUrl;
        Dictionary<string, string> queryParams;

        if (dto.QueryParams is not null)
        {
            baseUrl = rawUrl;
            queryParams = dto.QueryParams;
        }
        else
        {
            // Backwards compat: derive query params by parsing the legacy URL field.
            baseUrl = rawUrl.Contains('?') ? rawUrl[..rawUrl.IndexOf('?')] : rawUrl;
            var parsed = QueryStringHelper.ParseQueryParams(rawUrl);
            queryParams = new Dictionary<string, string>(parsed);
        }

        return new CollectionRequest
        {
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath),
            Method = new HttpMethod(dto.Method ?? HttpMethod.Get.Method),
            Url = baseUrl,
            Description = dto.Description,
            Headers = dto.Headers ?? new Dictionary<string, string>(),
            PathParams = dto.PathParams ?? new Dictionary<string, string>(),
            QueryParams = queryParams,
            BodyType = dto.BodyType ?? CollectionRequest.BodyTypes.None,
            Body = dto.Body,
            Auth = new AuthConfig
            {
                AuthType = dto.AuthType ?? AuthConfig.AuthTypes.None,
                Token = dto.AuthToken,
                Username = dto.AuthUsername,
                Password = dto.AuthPassword,
                ApiKeyName = dto.AuthApiKeyName,
                ApiKeyValue = dto.AuthApiKeyValue,
                ApiKeyIn = dto.AuthApiKeyIn ?? AuthConfig.ApiKeyLocations.Header,
            },
        };
    }

    private static RequestFileDto RequestToDto(CollectionRequest request) =>
        new()
        {
            Method = request.Method.Method,
            Url = request.Url,
            Description = request.Description,
            Headers = request.Headers.Count > 0
                ? request.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                : null,
            PathParams = request.PathParams.Count > 0
                ? request.PathParams.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                : null,
            QueryParams = request.QueryParams.Count > 0
                ? request.QueryParams.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
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
            AuthPassword = request.Auth.Password,
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
        public Dictionary<string, string>? Headers { get; set; }
        public Dictionary<string, string>? PathParams { get; set; }
        public Dictionary<string, string>? QueryParams { get; set; }
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
}
