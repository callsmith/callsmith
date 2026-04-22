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

    /// <summary>File name of the optional metadata manifest written in each folder.</summary>
    public const string MetaFileName = "_meta.json";

    /// <summary>Folder names to exclude from collection scanning (case-insensitive).</summary>
    /// <remarks>
    /// This list is intentionally short: we only exclude folders whose presence inside a
    /// collection directory would be very common yet are clearly non-request content. A longer
    /// pattern-based exclusion list was considered and rejected — the cost of maintaining it
    /// outweighs the benefit, and users can always keep unrelated content outside their
    /// collection root. If performance becomes a concern for very deep trees, a file-system
    /// watcher cache (rather than a full rescan) is the correct solution.
    /// </remarks>
    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Version control
        ".git", ".svn", ".hg", ".bzr",
    };

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

        // ReadFolder performs a synchronous recursive directory scan. This is intentional:
        // - Collections are local filesystem directories (no network latency).
        // - The sync I/O is offloaded to the thread pool via Task.Run so the UI thread is
        //   never blocked.
        // - A lazy/incremental load was considered but rejected at this stage: the tree view
        //   requires the full file list for ordering and auth resolution, so a partial load
        //   would only shift complexity without reducing total I/O.
        // If profiling shows that very large collections (thousands of files) are slow to open,
        // a FileSystemWatcher-based incremental update approach should be added at that point.
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
        var dto = JsonSerializer.Deserialize<RequestFileDto>(json, CallsmithJsonOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialise request file: '{filePath}'");

        if (dto.RequestId is null)
        {
            dto.RequestId = Guid.NewGuid();
            var updatedJson = JsonSerializer.Serialize(dto, CallsmithJsonOptions.Default);
            await File.WriteAllTextAsync(filePath, updatedJson, ct);
        }

        _logger.LogDebug("Loaded request from '{FilePath}'", filePath);

        // After the migration block above, RequestId is guaranteed to be non-null.
        // Use it as the stable key for secret storage so secrets survive file renames.
        var requestKey = dto.RequestId!.Value.ToString();

        string? basicAuthPassword = null;
        if ((dto.AuthType ?? AuthConfig.AuthTypes.None) == AuthConfig.AuthTypes.Basic
            && !string.IsNullOrEmpty(_currentRoot))
        {
            // Prefer the secret-stored value; fall back to whatever is in the file (migration path).
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, ISecretStorageService.AuthNamespace, requestKey, ct)
                .ConfigureAwait(false);
            basicAuthPassword = stored ?? dto.AuthPassword;
        }

        string? apiKeyValue = null;
        if ((dto.AuthType ?? AuthConfig.AuthTypes.None) == AuthConfig.AuthTypes.ApiKey
            && !string.IsNullOrEmpty(_currentRoot))
        {
            // Prefer the secret-stored value; fall back to whatever is in the file (migration path).
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, ISecretStorageService.AuthNamespace, requestKey, ct)
                .ConfigureAwait(false);
            apiKeyValue = stored ?? dto.AuthApiKeyValue;
        }

        string? bearerToken = null;
        if ((dto.AuthType ?? AuthConfig.AuthTypes.None) == AuthConfig.AuthTypes.Bearer
            && !string.IsNullOrEmpty(_currentRoot))
        {
            // Prefer the secret-stored value; fall back to whatever is in the file (migration path).
            // Only inject when the stored value is non-empty — empty means nothing has been saved yet.
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, ISecretStorageService.AuthNamespace, requestKey, ct)
                .ConfigureAwait(false);
            bearerToken = !string.IsNullOrEmpty(stored) ? stored : dto.AuthToken;
        }

        return DtoToRequest(dto, filePath, basicAuthPassword, apiKeyValue, bearerToken);
    }

    /// <inheritdoc/>
    public async Task SaveRequestAsync(CollectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var directory = Path.GetDirectoryName(request.FilePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path '{request.FilePath}'");

        Directory.CreateDirectory(directory);

        // Determine the stable request ID to use as the secret storage key.
        // If the request does not yet have an ID (legacy / first save), generate one now so
        // both the on-disk file and the secret entry share the same identifier.
        var requestId = request.RequestId ?? Guid.NewGuid();
        var requestKey = requestId.ToString();

        // Persist Basic auth password locally so it is never written into the collection file.
        if (request.Auth.AuthType == AuthConfig.AuthTypes.Basic && !string.IsNullOrEmpty(_currentRoot))
        {
            await _secrets
                .SetSecretAsync(
                    _currentRoot,
                    ISecretStorageService.AuthNamespace,
                    requestKey,
                    request.Auth.Password ?? string.Empty,
                    ct)
                .ConfigureAwait(false);
        }

        // Persist API key value locally so it is never written into the collection file.
        if (request.Auth.AuthType == AuthConfig.AuthTypes.ApiKey && !string.IsNullOrEmpty(_currentRoot))
        {
            await _secrets
                .SetSecretAsync(
                    _currentRoot,
                    ISecretStorageService.AuthNamespace,
                    requestKey,
                    request.Auth.ApiKeyValue ?? string.Empty,
                    ct)
                .ConfigureAwait(false);
        }

        // Persist bearer token locally so it is never written into the collection file.
        // Bearer tokens are long-lived credentials that must not be committed to version control.
        if (request.Auth.AuthType == AuthConfig.AuthTypes.Bearer && !string.IsNullOrEmpty(_currentRoot))
        {
            await _secrets
                .SetSecretAsync(
                    _currentRoot,
                    ISecretStorageService.AuthNamespace,
                    requestKey,
                    request.Auth.Token ?? string.Empty,
                    ct)
                .ConfigureAwait(false);
        }

        var dto = RequestToDto(request, requestId);
        var json = JsonSerializer.Serialize(dto, CallsmithJsonOptions.Default);

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
            RequestId = existing.RequestId,
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
            MultipartFormFiles = existing.MultipartFormFiles,
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
            RequestId = request.RequestId,
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
            MultipartFormFiles = request.MultipartFormFiles,
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
            RequestId = Guid.NewGuid(),
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

    /// <inheritdoc/>
    public Task<CollectionFolder> MoveFolderAsync(
        string folderPath, string destinationParentPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(destinationParentPath);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: '{folderPath}'");

        if (!Directory.Exists(destinationParentPath))
            throw new DirectoryNotFoundException($"Destination folder not found: '{destinationParentPath}'");

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var newPath = Path.Combine(destinationParentPath, folderName);

        if (Directory.Exists(newPath))
            throw new InvalidOperationException($"A folder named '{folderName}' already exists in the destination.");

        ct.ThrowIfCancellationRequested();
        Directory.Move(folderPath, newPath);
        _logger.LogDebug("Moved folder '{OldPath}' → '{NewPath}'", folderPath, newPath);

        return Task.FromResult(new CollectionFolder
        {
            FolderPath = newPath,
            Name = folderName,
            Requests = [],
            SubFolders = [],
        });
    }

    // -------------------------------------------------------------------------
    // Private — folder traversal
    // -------------------------------------------------------------------------

    private CollectionFolder ReadFolder(string folderPath)
    {
        var meta = ReadMetaFile(Path.Combine(folderPath, MetaFileName));

        var requests = Directory
            .EnumerateFiles(folderPath, $"*{RequestFileExtension}", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(filePath =>
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var dto = JsonSerializer.Deserialize<RequestFileDto>(json, CallsmithJsonOptions.Default);
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
            .Where(d => !ShouldExcludeFolder(Path.GetFileName(d)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(ReadFolder)
            .ToList();

        return new CollectionFolder
        {
            FolderPath = folderPath,
            Name = Path.GetFileName(folderPath),
            Requests = requests,
            SubFolders = subFolders,
            ItemOrder = meta.Order,
            Auth = meta.Auth,
        };
    }

    /// <summary>
    /// Determines if a folder should be excluded from collection scanning.
    /// Excludes the environment folder and common development/system directories.
    /// </summary>
    private static bool ShouldExcludeFolder(string folderName)
    {
        return string.Equals(folderName, EnvironmentFolderName, StringComparison.OrdinalIgnoreCase)
            || ExcludedFolderNames.Contains(folderName);
    }

    private static FolderMeta ReadMetaFile(string metaFilePath)
    {
        if (!File.Exists(metaFilePath))
            return FolderMeta.Empty;
        try
        {
            var json = File.ReadAllText(metaFilePath);
            var dto = JsonSerializer.Deserialize<FolderMetaDto>(json) ?? new FolderMetaDto();
            return FolderMeta.FromDto(dto);
        }
        catch (JsonException)
        {
            return FolderMeta.Empty;
        }
    }

    /// <inheritdoc/>
    public async Task SaveFolderOrderAsync(
        string folderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(orderedNames);

        var metaFilePath = Path.Combine(folderPath, MetaFileName);

        // Read existing meta to preserve the auth field.
        var existing = ReadMetaFile(metaFilePath);

        if (orderedNames.Count == 0 && existing.Auth.AuthType == AuthConfig.AuthTypes.Inherit)
        {
            // Both order and auth are default — remove the file entirely.
            if (File.Exists(metaFilePath))
            {
                File.Delete(metaFilePath);
                _logger.LogDebug("Removed meta file for '{FolderPath}'", folderPath);
            }
            return;
        }

        var dto = BuildMetaDto(orderedNames, existing.Auth);
        var json = JsonSerializer.Serialize(dto, CallsmithJsonOptions.Default);
        await File.WriteAllTextAsync(metaFilePath, json, ct);
        _logger.LogDebug("Saved folder order for '{FolderPath}'", folderPath);
    }

    /// <inheritdoc/>
    public async Task SaveFolderAuthAsync(
        string folderPath, AuthConfig auth, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(auth);

        // Always clean up any previously-stored folder auth secret when the type changes.
        // This prevents stale credentials from lingering in secret storage.
        if (!string.IsNullOrEmpty(_currentRoot))
        {
            var folderKey = FolderSecretKey(folderPath);
            if (auth.AuthType is not AuthConfig.AuthTypes.Basic
                    and not AuthConfig.AuthTypes.ApiKey
                    and not AuthConfig.AuthTypes.Bearer)
            {
                // Auth cleared or switched to a type that has no secret — remove any stored secret.
                await _secrets.DeleteSecretAsync(
                    _currentRoot, ISecretStorageService.FolderAuthNamespace, folderKey, ct)
                    .ConfigureAwait(false);
            }
            else if (auth.AuthType == AuthConfig.AuthTypes.Basic)
            {
                await _secrets.SetSecretAsync(
                    _currentRoot, ISecretStorageService.FolderAuthNamespace, folderKey,
                    auth.Password ?? string.Empty, ct)
                    .ConfigureAwait(false);
            }
            else if (auth.AuthType == AuthConfig.AuthTypes.ApiKey)
            {
                await _secrets.SetSecretAsync(
                    _currentRoot, ISecretStorageService.FolderAuthNamespace, folderKey,
                    auth.ApiKeyValue ?? string.Empty, ct)
                    .ConfigureAwait(false);
            }
            else if (auth.AuthType == AuthConfig.AuthTypes.Bearer)
            {
                // Bearer tokens are long-lived credentials that must not be committed to version control.
                await _secrets.SetSecretAsync(
                    _currentRoot, ISecretStorageService.FolderAuthNamespace, folderKey,
                    auth.Token ?? string.Empty, ct)
                    .ConfigureAwait(false);
            }
        }

        var metaFilePath = Path.Combine(folderPath, MetaFileName);

        // Read existing meta to preserve the order field.
        var existing = ReadMetaFile(metaFilePath);

        if (auth.AuthType == AuthConfig.AuthTypes.Inherit && existing.Order.Count == 0)
        {
            // Both auth and order are default — remove the file entirely.
            if (File.Exists(metaFilePath))
            {
                File.Delete(metaFilePath);
                _logger.LogDebug("Removed meta file (auth cleared) for '{FolderPath}'", folderPath);
            }
            return;
        }

        var dto = BuildMetaDto(existing.Order, auth);
        var json = JsonSerializer.Serialize(dto, CallsmithJsonOptions.Default);
        await File.WriteAllTextAsync(metaFilePath, json, ct);
        _logger.LogDebug("Saved folder auth for '{FolderPath}'", folderPath);
    }

    /// <inheritdoc/>
    public async Task<AuthConfig> LoadFolderAuthAsync(
        string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        var metaFilePath = Path.Combine(folderPath, MetaFileName);
        var meta = ReadMetaFile(metaFilePath);

        if (meta.Auth.AuthType == AuthConfig.AuthTypes.Inherit)
            return meta.Auth;

        return await EnrichAuthWithSecretsAsync(meta.Auth, folderPath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AuthConfig> ResolveEffectiveAuthAsync(
        string requestFilePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestFilePath);

        var dir = Path.GetDirectoryName(requestFilePath);
        if (string.IsNullOrEmpty(dir))
            return new AuthConfig { AuthType = AuthConfig.AuthTypes.None };

        var root = string.IsNullOrEmpty(_currentRoot)
            ? null
            : Path.GetFullPath(_currentRoot);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var metaPath = Path.Combine(dir, MetaFileName);
            var meta = ReadMetaFile(metaPath);

            if (meta.Auth.AuthType != AuthConfig.AuthTypes.Inherit)
            {
                // Enrich the resolved auth with sensitive values from secret storage.
                return await EnrichAuthWithSecretsAsync(meta.Auth, dir, ct).ConfigureAwait(false);
            }

            // If we've reached (or passed) the collection root, stop.
            var fullDir = Path.GetFullPath(dir);
            if (root is not null && string.Equals(fullDir, root, StringComparison.OrdinalIgnoreCase))
                return new AuthConfig { AuthType = AuthConfig.AuthTypes.None };

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                return new AuthConfig { AuthType = AuthConfig.AuthTypes.None };

            dir = parent;
        }
    }

    // -------------------------------------------------------------------------
    // Private — meta file helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a stable, opaque key used to address a folder's auth secret in
    /// <see cref="ISecretStorageService"/>. Derived from the normalised absolute folder path.
    /// </summary>
    private static string FolderSecretKey(string folderPath) =>
        FileSystemHelper.HashCollectionPath(folderPath);

    /// <summary>
    /// Returns a copy of <paramref name="auth"/> with the sensitive fields (password / API key
    /// value) populated from local secret storage. Falls back to whatever value is in the auth
    /// object itself (migration path for files written before this feature was introduced).
    /// </summary>
    private async Task<AuthConfig> EnrichAuthWithSecretsAsync(
        AuthConfig auth, string folderPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentRoot))
            return auth;

        var folderKey = FolderSecretKey(folderPath);

        if (auth.AuthType == AuthConfig.AuthTypes.Basic)
        {
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, ISecretStorageService.FolderAuthNamespace, folderKey, ct)
                .ConfigureAwait(false);
            // Only inject when the stored value is non-empty — empty means nothing has been saved yet.
            // Fall back to value in file (migration path for pre-upgrade _meta.json files).
            var password = !string.IsNullOrEmpty(stored) ? stored : auth.Password;
            if (password != auth.Password)
            {
                return new AuthConfig
                {
                    AuthType = auth.AuthType,
                    Token = auth.Token,
                    Username = auth.Username,
                    Password = password,
                    ApiKeyName = auth.ApiKeyName,
                    ApiKeyValue = auth.ApiKeyValue,
                    ApiKeyIn = auth.ApiKeyIn,
                };
            }
        }
        else if (auth.AuthType == AuthConfig.AuthTypes.ApiKey)
        {
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, ISecretStorageService.FolderAuthNamespace, folderKey, ct)
                .ConfigureAwait(false);
            // Only inject when the stored value is non-empty — empty means nothing has been saved yet.
            // Fall back to value in file (migration path for pre-upgrade _meta.json files).
            var apiKeyValue = !string.IsNullOrEmpty(stored) ? stored : auth.ApiKeyValue;
            if (apiKeyValue != auth.ApiKeyValue)
            {
                return new AuthConfig
                {
                    AuthType = auth.AuthType,
                    Token = auth.Token,
                    Username = auth.Username,
                    Password = auth.Password,
                    ApiKeyName = auth.ApiKeyName,
                    ApiKeyValue = apiKeyValue,
                    ApiKeyIn = auth.ApiKeyIn,
                };
            }
        }
        else if (auth.AuthType == AuthConfig.AuthTypes.Bearer)
        {
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, ISecretStorageService.FolderAuthNamespace, folderKey, ct)
                .ConfigureAwait(false);
            // Only inject when the stored value is non-empty — empty means nothing has been saved yet.
            // Fall back to value in file (migration path for pre-upgrade _meta.json files).
            var token = !string.IsNullOrEmpty(stored) ? stored : auth.Token;
            if (token != auth.Token)
            {
                return new AuthConfig
                {
                    AuthType = auth.AuthType,
                    Token = token,
                    Username = auth.Username,
                    Password = auth.Password,
                    ApiKeyName = auth.ApiKeyName,
                    ApiKeyValue = auth.ApiKeyValue,
                    ApiKeyIn = auth.ApiKeyIn,
                };
            }
        }

        return auth;
    }

    private static FolderMetaDto BuildMetaDto(IReadOnlyList<string> order, AuthConfig auth)
    {
        var dto = new FolderMetaDto();

        if (order.Count > 0)
            dto.Order = [..order];

        if (auth.AuthType != AuthConfig.AuthTypes.Inherit)
        {
            dto.Auth = new FolderMetaAuthDto
            {
                Type = auth.AuthType,
                // Bearer tokens are stored in local secret storage, never in the file.
                Token = auth.AuthType == AuthConfig.AuthTypes.Bearer ? null : auth.Token,
                Username = auth.Username,
                ApiKeyName = auth.ApiKeyName,
                ApiKeyIn = auth.ApiKeyIn == AuthConfig.ApiKeyLocations.Header ? null : auth.ApiKeyIn,
                // Password and ApiKeyValue are stored in local secret storage, never in the file.
                // See SaveFolderAuthAsync / LoadFolderAuthAsync.
            };
        }

        return dto;
    }

    // -------------------------------------------------------------------------
    // Private — mapping between domain model and on-disk DTO
    // -------------------------------------------------------------------------

    private static CollectionRequest DtoToRequest(RequestFileDto dto, string filePath, string? basicAuthPassword = null, string? apiKeyValue = null, string? bearerToken = null)
    {
        var rawUrl = dto.Url ?? string.Empty;
        
        IReadOnlyList<RequestKv> queryParams = [];
        if (dto.QueryParamEntries is not null)
        {
            queryParams = dto.QueryParamEntries
                .Select(e => new RequestKv(e.Name, e.Value, e.Enabled))
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

        var multipartFiles = dto.MultipartFileEntries is not null
            ? dto.MultipartFileEntries
                .Where(e => e.FileBytes is not null)
                .Select(e => new MultipartFilePart
                {
                    Key = e.Name,
                    FileBytes = e.FileBytes!,
                    FileName = e.FileName,
                    FilePath = e.FilePath,
                    IsEnabled = e.Enabled,
                })
                .ToList()
            : (IReadOnlyList<MultipartFilePart>)[];

        return new CollectionRequest
        {
            RequestId = dto.RequestId,
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath),
            Method = new HttpMethod(dto.Method ?? HttpMethod.Get.Method),
            Url = rawUrl,
            Description = dto.Description,
            Headers = headers,
            PathParams = dto.PathParams ?? new Dictionary<string, string>(),
            QueryParams = queryParams,
            BodyType = dto.BodyType ?? CollectionRequest.BodyTypes.None,
            Body = dto.Body,
            FileBodyBase64 = dto.FileBodyBase64,
            FileBodyName = dto.FileBodyName,
            FormParams = formParams,
            MultipartFormFiles = multipartFiles,
            Auth = new AuthConfig
            {
                AuthType = dto.AuthType ?? AuthConfig.AuthTypes.Inherit,
                Token = bearerToken ?? dto.AuthToken,
                Username = dto.AuthUsername,
                Password = basicAuthPassword ?? dto.AuthPassword,
                ApiKeyName = dto.AuthApiKeyName,
                ApiKeyValue = apiKeyValue ?? dto.AuthApiKeyValue,
                ApiKeyIn = dto.AuthApiKeyIn ?? AuthConfig.ApiKeyLocations.Header,
            },
        };
    }

    private static RequestFileDto RequestToDto(CollectionRequest request, Guid requestId) =>
        new()
        {
            RequestId = requestId,
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
            MultipartFileEntries = request.MultipartFormFiles.Count > 0
                ? request.MultipartFormFiles
                    .Select(p => new MultipartFileEntryDto
                    {
                        Name = p.Key,
                        FileName = p.FileName,
                        FilePath = p.FilePath,
                        FileBytes = p.FileBytes,
                        Enabled = p.IsEnabled,
                    })
                    .ToList()
                : null,
            BodyType = request.BodyType == CollectionRequest.BodyTypes.None
                ? null
                : request.BodyType,
            Body = request.Body,
            FileBodyBase64 = request.FileBodyBase64,
            FileBodyName = request.FileBodyName,
            AuthType = request.Auth.AuthType == AuthConfig.AuthTypes.Inherit
                ? null
                : request.Auth.AuthType,
            // Bearer tokens are stored in local secret storage, never in the file.
            AuthToken = request.Auth.AuthType == AuthConfig.AuthTypes.Bearer
                ? null
                : request.Auth.Token,
            AuthUsername = request.Auth.Username,
            // Basic auth passwords are stored in local secret storage, never in the file.
            AuthPassword = request.Auth.AuthType == AuthConfig.AuthTypes.Basic
                ? null
                : request.Auth.Password,
            AuthApiKeyName = request.Auth.ApiKeyName,
            // API key values are stored in local secret storage, never in the file.
            AuthApiKeyValue = request.Auth.AuthType == AuthConfig.AuthTypes.ApiKey
                ? null
                : request.Auth.ApiKeyValue,
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
        public Guid? RequestId { get; set; }
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
        public List<MultipartFileEntryDto>? MultipartFileEntries { get; set; }
        public string? BodyType { get; set; }
        public string? Body { get; set; }
        public string? FileBodyBase64 { get; set; }
        public string? FileBodyName { get; set; }
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

    private sealed class MultipartFileEntryDto
    {
        public string Name { get; set; } = string.Empty;
        public string? FileName { get; set; }
        public string? FilePath { get; set; }
        public byte[]? FileBytes { get; set; }
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// The JSON structure written to and read from each <c>_meta.json</c> folder metadata file.
    /// </summary>
    private sealed class FolderMetaDto
    {
        [JsonPropertyName("order")]
        public List<string>? Order { get; set; }

        [JsonPropertyName("auth")]
        public FolderMetaAuthDto? Auth { get; set; }
    }

    /// <summary>Auth section within <c>_meta.json</c>.</summary>
    private sealed class FolderMetaAuthDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        [JsonPropertyName("apiKeyName")]
        public string? ApiKeyName { get; set; }

        [JsonPropertyName("apiKeyValue")]
        public string? ApiKeyValue { get; set; }

        /// <summary>Null means header (the default); explicit "query" means query string.</summary>
        [JsonPropertyName("apiKeyIn")]
        public string? ApiKeyIn { get; set; }
    }

    /// <summary>Parsed result of a <c>_meta.json</c> file.</summary>
    private sealed record FolderMeta(IReadOnlyList<string> Order, AuthConfig Auth)
    {
        public static FolderMeta Empty { get; } = new([], new AuthConfig());

        public static FolderMeta FromDto(FolderMetaDto dto)
        {
            IReadOnlyList<string> order = dto.Order ?? [];
            AuthConfig auth;

            if (dto.Auth is { Type: not null } a)
            {
                auth = new AuthConfig
                {
                    AuthType = a.Type,
                    Token = a.Token,
                    Username = a.Username,
                    Password = a.Password,
                    ApiKeyName = a.ApiKeyName,
                    ApiKeyValue = a.ApiKeyValue,
                    ApiKeyIn = a.ApiKeyIn ?? AuthConfig.ApiKeyLocations.Header,
                };
            }
            else
            {
                auth = new AuthConfig();
            }

            return new FolderMeta(order, auth);
        }
    }
}
