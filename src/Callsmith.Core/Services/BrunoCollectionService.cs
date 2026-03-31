using System.Net.Http;
using System.Text.Json;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Bruno;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="ICollectionService"/> implementation that reads and writes Bruno <c>.bru</c>
/// request files, maintaining full round-trip fidelity so that both Callsmith and Bruno
/// users can work on the same collection simultaneously.
/// <para>
/// On every save the existing file (if present) is re-read and only blocks owned by
/// Callsmith's request model are rewritten. All other blocks are preserved unchanged so
/// Bruno-specific metadata/tests/asserts are not polluted by Callsmith saves.
/// </para>
/// </summary>
public sealed class BrunoCollectionService : ICollectionService
{
    public const string RequestFileExtension = ".bru";
    string ICollectionService.RequestFileExtension => RequestFileExtension;

    /// <summary>Environment sub-folder name used by Bruno (plural, unlike Callsmith's singular).</summary>
    public const string EnvironmentFolderName = "environments";

    /// <summary>Folder names to exclude from collection scanning (case-insensitive).</summary>
    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Version control
        ".git", ".svn", ".hg", ".bzr",
    };

    private static readonly string[] _httpVerbs =
        ["get", "post", "put", "delete", "patch", "head", "options"];

    /// <summary>Reserved namespace used inside <see cref="ISecretStorageService"/> for Basic auth passwords.</summary>
    private const string AuthSecretsNamespace = "__auth__";

    private readonly ISecretStorageService _secrets;
    private readonly ILogger<BrunoCollectionService> _logger;

    // Populated by OpenFolderAsync; used to key auth secrets per-collection.
    private string _currentRoot = string.Empty;

    public BrunoCollectionService(
        ISecretStorageService secrets,
        ILogger<BrunoCollectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = secrets;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Open / Load
    // ─────────────────────────────────────────────────────────────────────────

    public Task<CollectionFolder> OpenFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Collection folder not found: '{folderPath}'");

        ct.ThrowIfCancellationRequested();
        _currentRoot = Path.GetFullPath(folderPath);
        var folder = Task.Run(() => ReadFolder(folderPath, isRoot: true), ct);
        _logger.LogDebug("Opened Bruno collection at '{FolderPath}'", folderPath);
        return folder;
    }

    public async Task<CollectionRequest> LoadRequestAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Request file not found: '{filePath}'", filePath);

        var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var doc = BruParser.Parse(text);

        var request = DocToRequest(doc, filePath)
               ?? throw new InvalidOperationException($"Not a valid HTTP request file: '{filePath}'");

        if (request.Auth.AuthType == AuthConfig.AuthTypes.Basic && !string.IsNullOrEmpty(_currentRoot))
        {
            // Secret is keyed by file path relative to the collection root so renames and moves
            // can migrate keys without touching the .bru file.
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, AuthSecretsNamespace, GetAuthSecretKey(filePath), ct)
                .ConfigureAwait(false);
            if (stored is not null || request.Auth.Password is not null)
            {
                request = DocToRequest(doc, filePath, stored ?? request.Auth.Password)
                    ?? request;
            }
        }

        return request;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Save / Create / Delete / Rename
    // ─────────────────────────────────────────────────────────────────────────

    public async Task SaveRequestAsync(CollectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var directory = Path.GetDirectoryName(request.FilePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for '{request.FilePath}'");
        Directory.CreateDirectory(directory);

        // Re-read the existing file to preserve scripts, tests, and disabled items.
        BruDocument? existing = null;
        if (File.Exists(request.FilePath))
        {
            try
            {
                var existingText = await File.ReadAllTextAsync(request.FilePath, ct).ConfigureAwait(false);
                existing = BruParser.Parse(existingText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not re-read '{Path}' for round-trip; starting fresh", request.FilePath);
            }
        }

        // Persist Basic auth password locally so it is never written into the collection file.
        // The secret is keyed by file path relative to the collection root.
        if (request.Auth.AuthType == AuthConfig.AuthTypes.Basic && !string.IsNullOrEmpty(_currentRoot))
        {
            await _secrets
                .SetSecretAsync(
                    _currentRoot,
                    AuthSecretsNamespace,
                    GetAuthSecretKey(request.FilePath),
                    request.Auth.Password ?? string.Empty,
                    ct)
                .ConfigureAwait(false);
        }

        var content = BuildBruContent(request, existing);
        await File.WriteAllTextAsync(request.FilePath, content, ct).ConfigureAwait(false);
        _logger.LogDebug("Saved Bruno request to '{FilePath}'", request.FilePath);
    }

    public Task DeleteRequestAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Request file not found: '{filePath}'", filePath);

        File.Delete(filePath);
        _logger.LogDebug("Deleted Bruno request file '{FilePath}'", filePath);
        return Task.CompletedTask;
    }

    public async Task<CollectionRequest> RenameRequestAsync(
        string filePath, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(newName);
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New name must not be empty.", nameof(newName));
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Request file not found: '{filePath}'", filePath);

        var directory = Path.GetDirectoryName(filePath)!;
        var newFilePath = Path.Combine(directory, SanitizeFileName(newName) + RequestFileExtension);

        if (File.Exists(newFilePath) && !string.Equals(filePath, newFilePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A request named '{newName}' already exists in this folder.");

        // Update meta.name in the file content, then rename the file.
        var existingText = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var doc = BruParser.Parse(existingText);
        SetMetaName(doc, newName);
        var newContent = BruWriter.Write(doc.Blocks, doc.LineEnding);
        await File.WriteAllTextAsync(newFilePath, newContent, ct).ConfigureAwait(false);

        if (!string.Equals(filePath, newFilePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(filePath);
            await MigrateAuthSecretAsync(filePath, newFilePath, ct).ConfigureAwait(false);
        }

        _logger.LogDebug("Renamed Bruno request '{OldPath}' → '{NewPath}'", filePath, newFilePath);

        var existing = await LoadRequestAsync(newFilePath, ct).ConfigureAwait(false);
        return existing;
    }

    public async Task<CollectionRequest> MoveRequestAsync(
        string filePath,
        string destinationFolderPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(destinationFolderPath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Request file not found: '{filePath}'", filePath);

        var destinationDirectory = destinationFolderPath;
        Directory.CreateDirectory(destinationDirectory);

        var fileName = Path.GetFileName(filePath);
        var destinationFilePath = Path.Combine(destinationDirectory, fileName);

        if (File.Exists(destinationFilePath))
            throw new InvalidOperationException($"A request named '{fileName}' already exists in destination folder.");

        ct.ThrowIfCancellationRequested();
        File.Move(filePath, destinationFilePath);
        await MigrateAuthSecretAsync(filePath, destinationFilePath, ct).ConfigureAwait(false);

        var moved = await LoadRequestAsync(destinationFilePath, ct).ConfigureAwait(false);

        _logger.LogDebug("Moved Bruno request '{OldPath}' → '{NewPath}'", filePath, destinationFilePath);

        return moved;
    }

    public async Task<CollectionRequest> CreateRequestAsync(
        string folderPath, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        Directory.CreateDirectory(folderPath);

        var filePath = Path.Combine(folderPath, SanitizeFileName(name) + RequestFileExtension);
        if (File.Exists(filePath))
            throw new InvalidOperationException($"A request named '{name}' already exists in this folder.");

        var nextSeq = ComputeNextSeq(folderPath);
        var content = BuildNewRequestContent(name, nextSeq);
        await File.WriteAllTextAsync(filePath, content, ct).ConfigureAwait(false);
        _logger.LogDebug("Created Bruno request '{FilePath}'", filePath);

        return await LoadRequestAsync(filePath, ct).ConfigureAwait(false);
    }

    public async Task<CollectionFolder> CreateFolderAsync(
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

        var folderBruContent = $"meta {{\n  name: {name}\n}}\n";
        await File.WriteAllTextAsync(Path.Combine(newPath, "folder.bru"), folderBruContent, ct).ConfigureAwait(false);

        _logger.LogDebug("Created Bruno folder '{FolderPath}'", newPath);
        return new CollectionFolder { FolderPath = newPath, Name = name, Requests = [], SubFolders = [] };
    }

    public async Task<CollectionFolder> RenameFolderAsync(
        string folderPath, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(newName);
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New name must not be empty.", nameof(newName));
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: '{folderPath}'");

        var parent = Path.GetDirectoryName(folderPath)!;
        var newPath = Path.Combine(parent, newName);
        if (Directory.Exists(newPath))
            throw new InvalidOperationException($"A folder named '{newName}' already exists.");

        ct.ThrowIfCancellationRequested();
        Directory.Move(folderPath, newPath);

        // Update folder.bru meta.name if the descriptor exists.
        var folderBruPath = Path.Combine(newPath, "folder.bru");
        if (File.Exists(folderBruPath))
        {
            try
            {
                var text = await File.ReadAllTextAsync(folderBruPath, ct).ConfigureAwait(false);
                var doc = BruParser.Parse(text);
                SetMetaName(doc, newName);
                await File.WriteAllTextAsync(folderBruPath, BruWriter.Write(doc.Blocks, doc.LineEnding), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not update folder.bru name for '{Path}'", newPath);
            }
        }

        _logger.LogDebug("Renamed Bruno folder '{OldPath}' → '{NewPath}'", folderPath, newPath);
        return new CollectionFolder { FolderPath = newPath, Name = newName, Requests = [], SubFolders = [] };
    }

    public Task DeleteFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: '{folderPath}'");

        ct.ThrowIfCancellationRequested();
        FileSystemHelper.DeleteDirectoryRobust(folderPath);
        _logger.LogDebug("Deleted Bruno folder '{FolderPath}'", folderPath);
        return Task.CompletedTask;
    }

    public async Task SaveFolderOrderAsync(
        string folderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(orderedNames);

        // Assign seq values based on position. Both request files and folders receive seq numbers
        // so that mixed ordering is preserved when the collection is reloaded.
        var seq = 1;
        foreach (var name in orderedNames)
        {
            if (name.EndsWith(RequestFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                // Request file — update seq in the .bru file itself.
                var filePath = Path.Combine(folderPath, name);
                if (File.Exists(filePath))
                {
                    try
                    {
                        await UpdateSeqInFileAsync(filePath, seq, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update seq for '{File}'", filePath);
                    }
                }
            }
            else
            {
                // Folder — update seq in folder.bru inside the sub-folder.
                var subFolderPath = Path.Combine(folderPath, name);
                var folderBruPath = Path.Combine(subFolderPath, "folder.bru");
                if (File.Exists(folderBruPath))
                {
                    try
                    {
                        await UpdateSeqInFileAsync(folderBruPath, seq, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update seq for folder '{Folder}'", subFolderPath);
                    }
                }
            }
            seq++;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — folder traversal
    // ─────────────────────────────────────────────────────────────────────────

    private CollectionFolder ReadFolder(string folderPath, bool isRoot = false)
    {
        var requestFiles = Directory
            .EnumerateFiles(folderPath, "*.bru", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                var fname = Path.GetFileName(f);
                return !string.Equals(fname, "folder.bru", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fname, "collection.bru", StringComparison.OrdinalIgnoreCase);
            });

        var parsedRequests = new List<(CollectionRequest Request, int Seq)>();
        foreach (var filePath in requestFiles)
        {
            try
            {
                var text = File.ReadAllText(filePath);
                var doc = BruParser.Parse(text);
                var req = DocToRequest(doc, filePath);
                if (req is null) continue;

                var seqStr = doc.GetValue("meta", "seq");
                var seqVal = int.TryParse(seqStr, out var s) ? s : NoSeq;
                parsedRequests.Add((req, seqVal));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable .bru file: '{Path}'", filePath);
            }
        }

        // Read sub-folders including their seq from folder.bru meta.seq.
        var parsedFolders = new List<(CollectionFolder Folder, int Seq)>();
        foreach (var dirPath in Directory
                     .EnumerateDirectories(folderPath)
                     .Where(d => !ShouldExcludeFolder(Path.GetFileName(d))))
        {
            var folderSeq = GetFolderSeq(dirPath);
            parsedFolders.Add((ReadFolder(dirPath), folderSeq));
        }

        // Ordering rules:
        //  • Folders always come before requests.
        //  • Within each group (folders / requests):
        //      1. Items with no seq, sorted alphabetically.
        //      2. Items with a seq, sorted by seq value (gaps are allowed and preserved).
        var foldersNoSeq = parsedFolders
            .Where(f => f.Seq == NoSeq)
            .OrderBy(f => f.Folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var foldersWithSeq = parsedFolders
            .Where(f => f.Seq != NoSeq)
            .OrderBy(f => f.Seq)
            .ToList();

        var requestsNoSeq = parsedRequests
            .Where(r => r.Seq == NoSeq)
            .OrderBy(r => r.Request.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var requestsWithSeq = parsedRequests
            .Where(r => r.Seq != NoSeq)
            .OrderBy(r => r.Seq)
            .ToList();

        var sortedRequests = requestsNoSeq
            .Concat(requestsWithSeq)
            .Select(r => r.Request)
            .ToList();

        var subFolders = foldersNoSeq
            .Concat(foldersWithSeq)
            .Select(f => f.Folder)
            .ToList();

        // ItemOrder is the flat mixed list — folders first, then requests, each group with
        // no-seq items (alphabetical) before seq items (ordered by seq value).
        var itemOrder = foldersNoSeq.Select(f => f.Folder.Name)
            .Concat(foldersWithSeq.Select(f => f.Folder.Name))
            .Concat(requestsNoSeq.Select(r => Path.GetFileName(r.Request.FilePath)))
            .Concat(requestsWithSeq.Select(r => Path.GetFileName(r.Request.FilePath)))
            .ToList();

        return new CollectionFolder
        {
            FolderPath = folderPath,
            Name = isRoot ? GetRootDisplayName(folderPath) : GetFolderDisplayName(folderPath),
            Requests = sortedRequests,
            SubFolders = subFolders,
            ItemOrder = itemOrder,
        };
    }

    /// <summary>Sentinel value meaning "no seq assigned".</summary>
    private const int NoSeq = int.MinValue;

    private static int GetFolderSeq(string folderPath)
    {
        var folderBruPath = Path.Combine(folderPath, "folder.bru");
        if (!File.Exists(folderBruPath)) return NoSeq;
        try
        {
            var text = File.ReadAllText(folderBruPath);
            var doc = BruParser.Parse(text);
            var seqStr = doc.GetValue("meta", "seq");
            return int.TryParse(seqStr, out var s) ? s : NoSeq;
        }
        catch { return NoSeq; }
    }

    private static string GetRootDisplayName(string folderPath)
    {
        var brunoJsonPath = Path.Combine(folderPath, "bruno.json");
        if (!File.Exists(brunoJsonPath)) return Path.GetFileName(folderPath) ?? folderPath;
        try
        {
            var json = File.ReadAllText(brunoJsonPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var nameEl))
                return nameEl.GetString() ?? Path.GetFileName(folderPath)!;
        }
        catch { /* fall through */ }
        return Path.GetFileName(folderPath) ?? folderPath;
    }

    private static string GetFolderDisplayName(string folderPath)
    {
        var folderBruPath = Path.Combine(folderPath, "folder.bru");
        if (!File.Exists(folderBruPath)) return Path.GetFileName(folderPath) ?? folderPath;
        try
        {
            var text = File.ReadAllText(folderBruPath);
            var doc = BruParser.Parse(text);
            return doc.GetValue("meta", "name") ?? Path.GetFileName(folderPath)!;
        }
        catch { /* fall through */ }
        return Path.GetFileName(folderPath) ?? folderPath;
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

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — BruDocument ↔ CollectionRequest mapping
    // ─────────────────────────────────────────────────────────────────────────

    private CollectionRequest? DocToRequest(BruDocument doc, string filePath, string? basicPasswordOverride = null)
    {
        var meta = doc.Find("meta");
        if (meta is null) return null;

        string? httpMethod = null;
        BruBlock? methodBlock = null;
        foreach (var verb in _httpVerbs)
        {
            methodBlock = doc.Find(verb);
            if (methodBlock is null) continue;
            httpMethod = verb.ToUpperInvariant();
            break;
        }
        if (httpMethod is null) return null;

        var name = meta.GetValue("name") ?? Path.GetFileNameWithoutExtension(filePath);
        var rawUrl = methodBlock!.GetValue("url") ?? string.Empty;
        var bruBodyType = methodBlock.GetValue("body") ?? "none";
        var bruAuthType = methodBlock.GetValue("auth") ?? "none";

        // params:query block is authoritative for query params when present.
        IReadOnlyList<RequestKv> queryParams = [];
        var queryBlock = doc.Find("params:query");
        if (queryBlock?.Items.Count > 0)
        {
            // Preserve all items (enabled and disabled) in the domain model.
            queryParams = queryBlock.Items
                .Select(kv => new RequestKv(kv.Key, kv.Value, kv.IsEnabled))
                .ToList();
        }

        // Preserve all header items (enabled and disabled).
        var headers = doc.Find("headers")?.Items
            .Select(kv => new RequestKv(kv.Key, kv.Value, kv.IsEnabled))
            .ToList() ?? (IReadOnlyList<RequestKv>)[];

        var bodyType = MapBrunoBodyType(bruBodyType);
        var body = BuildBody(doc, bodyType);
        var allBodyContents = BuildAllBodyContents(doc);
        var formParams = BuildFormParams(doc, bodyType);
        var authType = MapBrunoAuthType(bruAuthType);
        var auth = BuildAuth(doc, authType, basicPasswordOverride);

        // params:path block — load enabled values into PathParams so the UI can edit them.
        var pathParamsBlock = doc.Find("params:path");
        var pathParams = pathParamsBlock?.Items
            .Where(kv => kv.IsEnabled)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>();

        // For Bruno requests, compute a stable request identity based on the display path
        // (folder path + meta.name). This identity is stable within the collection but is lost
        // on rename or move, meeting Issue 40 requirements.
        Guid? requestId;
        if (!string.IsNullOrEmpty(_currentRoot))
        {
            requestId = ComputeBrunoRequestIdentity(_currentRoot, filePath, name);
        }
        else
        {
            // Fall back to persisted requestId if present (for backwards compatibility during migration).
            requestId = TryGetRequestId(doc, out var persistedId) ? (Guid?)persistedId : null;
        }

        return new CollectionRequest
        {
            RequestId = requestId,
            FilePath = filePath,
            Name = name,
            Method = new HttpMethod(httpMethod),
            Url = rawUrl,
            Headers = headers,
            PathParams = pathParams,
            QueryParams = queryParams,
            BodyType = bodyType,
            Body = body,
            AllBodyContents = allBodyContents,
            FormParams = formParams,
            Auth = auth,
        };
    }

    private static string? BuildBody(BruDocument doc, string bodyType) => bodyType switch
    {
        CollectionRequest.BodyTypes.Json => doc.Find("body:json")?.RawContent,
        CollectionRequest.BodyTypes.Text => doc.Find("body:text")?.RawContent,
        CollectionRequest.BodyTypes.Xml => doc.Find("body:xml")?.RawContent,
        // Form and multipart params are stored in FormParams, not Body
        _ => null,
    };

    /// <summary>
    /// Reads ALL text body blocks present in the document — regardless of which body type is
    /// currently active — so the UI can restore editor content when the user switches types.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildAllBodyContents(BruDocument doc)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.Find("body:json")?.RawContent is { } json)
            result[CollectionRequest.BodyTypes.Json] = json;
        if (doc.Find("body:text")?.RawContent is { } text)
            result[CollectionRequest.BodyTypes.Text] = text;
        if (doc.Find("body:xml")?.RawContent is { } xml)
            result[CollectionRequest.BodyTypes.Xml] = xml;
        return result;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> BuildFormParams(
        BruDocument doc, string bodyType)
    {
        var blockName = bodyType switch
        {
            CollectionRequest.BodyTypes.Form => "body:form-urlencoded",
            CollectionRequest.BodyTypes.Multipart => "body:multipart",
            _ => null,
        };
        if (blockName is null) return [];
        var block = doc.Find(blockName);
        if (block is null) return [];
        return block.Items
            .Where(kv => kv.IsEnabled)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
            .ToList();
    }

    private static AuthConfig BuildAuth(BruDocument doc, string authType, string? basicPasswordOverride = null) => authType switch
    {
        AuthConfig.AuthTypes.Bearer => new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Bearer,
            Token = doc.GetValue("auth:bearer", "token"),
        },
        AuthConfig.AuthTypes.Basic => new AuthConfig
        {
            AuthType = AuthConfig.AuthTypes.Basic,
            Username = doc.GetValue("auth:basic", "username"),
            // Prefer injected secret; fall back to file value (migration path for existing files).
            Password = basicPasswordOverride ?? doc.GetValue("auth:basic", "password"),
        },
        _ => new AuthConfig { AuthType = AuthConfig.AuthTypes.None },
    };

    private static string MapBrunoBodyType(string brunoType) => brunoType switch
    {
        "json" => CollectionRequest.BodyTypes.Json,
        "text" => CollectionRequest.BodyTypes.Text,
        "xml" => CollectionRequest.BodyTypes.Xml,
        "formUrlEncoded" => CollectionRequest.BodyTypes.Form,
        "multipartForm" => CollectionRequest.BodyTypes.Multipart,
        "graphql" => CollectionRequest.BodyTypes.Json,   // closest available mapping
        _ => CollectionRequest.BodyTypes.None,
    };

    private static string MapCallsmithBodyType(string callsmithType) => callsmithType switch
    {
        CollectionRequest.BodyTypes.Json => "json",
        CollectionRequest.BodyTypes.Text => "text",
        CollectionRequest.BodyTypes.Xml => "xml",
        CollectionRequest.BodyTypes.Form => "formUrlEncoded",
        CollectionRequest.BodyTypes.Multipart => "multipartForm",
        _ => "none",
    };

    private static string MapBrunoAuthType(string brunoType) => brunoType switch
    {
        "bearer" => AuthConfig.AuthTypes.Bearer,
        "basic" => AuthConfig.AuthTypes.Basic,
        _ => AuthConfig.AuthTypes.None,
    };

    private static string MapCallsmithAuthType(string callsmithType) => callsmithType switch
    {
        AuthConfig.AuthTypes.Bearer => "bearer",
        AuthConfig.AuthTypes.Basic => "basic",
        _ => "none",
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — building .bru file content from CollectionRequest
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildBruContent(CollectionRequest request, BruDocument? existing)
    {
        // Block-targeted update: start from existing blocks (preserving original order and
        // non-owned content), then update only the specific blocks that Callsmith owns.
        // This prevents block reordering and avoids introducing whitespace changes.
        var blocks = existing is not null
            ? new List<BruBlock>(existing.Blocks)
            : new List<BruBlock>();

        var bruMethod = request.Method.Method.ToLowerInvariant();

        // meta — update in-place (or insert at top if not present)
        var metaBlock = new BruBlock("meta");
        metaBlock.Items.Add(new BruKv("name", request.Name));
        metaBlock.Items.Add(new BruKv("type", "http"));
        metaBlock.Items.Add(new BruKv("seq", existing?.GetValue("meta", "seq") ?? "1"));
        // requestId is intentionally omitted — Bruno does not use this field.
        SetOrInsertAt(blocks, "meta", metaBlock, 0);

        // HTTP method block — locate the existing verb block (any verb) and replace it
        // in-place so that block position is preserved even when the method changes.
        var methodBlock = new BruBlock(bruMethod);
        methodBlock.Items.Add(new BruKv("url", request.Url));
        methodBlock.Items.Add(new BruKv("body", MapCallsmithBodyType(request.BodyType)));
        methodBlock.Items.Add(new BruKv("auth", MapCallsmithAuthType(request.Auth.AuthType)));
        ReplaceVerbBlock(blocks, methodBlock);

        // params:query — update in-place or remove when empty
        if (request.QueryParams.Count > 0)
        {
            var queryBlock = new BruBlock("params:query");
            foreach (var p in request.QueryParams)
                queryBlock.Items.Add(new BruKv(p.Key, p.Value, p.IsEnabled));
            SetOrInsertAfter(blocks, "params:query", queryBlock, bruMethod);
        }
        else
        {
            RemoveBlock(blocks, "params:query");
        }

        // headers — update in-place or remove when empty
        if (request.Headers.Count > 0)
        {
            var headersBlock = new BruBlock("headers");
            foreach (var h in request.Headers)
                headersBlock.Items.Add(new BruKv(h.Key, h.Value, h.IsEnabled));
            SetOrInsertAfter(blocks, "headers", headersBlock, bruMethod);
        }
        else
        {
            RemoveBlock(blocks, "headers");
        }

        // body — update the active body block in-place; all other body blocks are left
        // untouched so that content is not lost when the user switches body types.
        UpdateBodyBlockInPlace(blocks, request, bruMethod);

        // auth — update auth block in-place
        UpdateAuthBlockInPlace(blocks, request, existing, bruMethod);

        // params:path — update in-place so that Callsmith edits are persisted and the UI
        // reflects current values, while preserving the block's original position.
        if (request.PathParams is { Count: > 0 })
        {
            var pathBlock = new BruBlock("params:path");
            foreach (var (k, v) in request.PathParams)
                pathBlock.Items.Add(new BruKv(k, v));
            SetOrInsertAfter(blocks, "params:path", pathBlock, "headers");
        }
        else
        {
            RemoveBlock(blocks, "params:path");
        }

        return BruWriter.Write(blocks, existing?.LineEnding ?? "\n");
    }

    private static void SetOrInsertAt(List<BruBlock> blocks, string name, BruBlock block, int fallbackIndex)
    {
        var idx = IndexOf(blocks, name);
        if (idx >= 0)
        {
            block.HasPrecedingBlankLine = blocks[idx].HasPrecedingBlankLine;
            blocks[idx] = block;
        }
        else
        {
            blocks.Insert(Math.Min(fallbackIndex, blocks.Count), block);
        }
    }

    /// <summary>
    /// Finds an existing block with <paramref name="name"/> and replaces it (preserving
    /// <see cref="BruBlock.HasPrecedingBlankLine"/> from the original), or inserts
    /// <paramref name="block"/> immediately after the block named <paramref name="afterName"/>.
    /// Falls back to appending at the end when neither block is found.
    /// </summary>
    private static void SetOrInsertAfter(List<BruBlock> blocks, string name, BruBlock block, string afterName)
    {
        var idx = IndexOf(blocks, name);
        if (idx >= 0)
        {
            block.HasPrecedingBlankLine = blocks[idx].HasPrecedingBlankLine;
            blocks[idx] = block;
            return;
        }
        var afterIdx = IndexOf(blocks, afterName);
        // New blocks inserted between existing ones get a preceding blank line for readability.
        block.HasPrecedingBlankLine = true;
        blocks.Insert(afterIdx >= 0 ? afterIdx + 1 : blocks.Count, block);
    }

    /// <summary>
    /// Finds any existing HTTP-verb block, replaces its name and content with
    /// <paramref name="block"/>, or appends after <c>meta</c> when none is found.
    /// </summary>
    private static void ReplaceVerbBlock(List<BruBlock> blocks, BruBlock block)
    {
        // Find the first existing verb block (method may have changed, e.g. GET → POST).
        var verbIdx = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (_httpVerbs.Contains(blocks[i].Name, StringComparer.OrdinalIgnoreCase))
            {
                verbIdx = i;
                break;
            }
        }

        if (verbIdx >= 0)
        {
            block.HasPrecedingBlankLine = blocks[verbIdx].HasPrecedingBlankLine;
            blocks[verbIdx] = block;
        }
        else
        {
            var metaIdx = IndexOf(blocks, "meta");
            block.HasPrecedingBlankLine = true;
            blocks.Insert(metaIdx >= 0 ? metaIdx + 1 : blocks.Count, block);
        }
    }

    /// <summary>Removes the first block with the given name, if present.</summary>
    private static void RemoveBlock(List<BruBlock> blocks, string name)
    {
        var idx = IndexOf(blocks, name);
        if (idx >= 0) blocks.RemoveAt(idx);
    }

    /// <summary>Returns the index of the first block matching <paramref name="name"/> (case-insensitive).</summary>
    private static int IndexOf(List<BruBlock> blocks, string name) =>
        blocks.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Updates body blocks for <paramref name="request"/> in <paramref name="blocks"/>.
    /// <para>
    /// The active body block is updated with <see cref="CollectionRequest.Body"/>.
    /// Non-active text body blocks that have content tracked in
    /// <see cref="CollectionRequest.AllBodyContents"/> are also updated so that edits made
    /// in the UI while a different body type was active are persisted.
    /// Body blocks with no tracked content are left unchanged (preserving any content
    /// written by external tools such as Bruno).
    /// </para>
    /// </summary>
    private static void UpdateBodyBlockInPlace(List<BruBlock> blocks, CollectionRequest request, string bruMethod)
    {
        var activeBodyBlockName = request.BodyType switch
        {
            CollectionRequest.BodyTypes.Json => "body:json",
            CollectionRequest.BodyTypes.Text => "body:text",
            CollectionRequest.BodyTypes.Xml => "body:xml",
            CollectionRequest.BodyTypes.Form => "body:form-urlencoded",
            CollectionRequest.BodyTypes.Multipart => "body:multipart",
            _ => null,
        };

        if (activeBodyBlockName is null)
            return; // body type is none — preserve all existing body blocks as-is

        BruBlock? newBodyBlock = null;

        if (request.BodyType is CollectionRequest.BodyTypes.Form or CollectionRequest.BodyTypes.Multipart)
        {
            if (request.FormParams.Count > 0)
            {
                newBodyBlock = new BruBlock(activeBodyBlockName);
                foreach (var (k, v) in request.FormParams)
                    newBodyBlock.Items.Add(new BruKv(k, v));
            }
        }
        else if (request.Body is not null)
        {
            newBodyBlock = new BruBlock(activeBodyBlockName);
            var raw = request.Body;
            newBodyBlock.RawContent = raw.StartsWith(' ') || raw.StartsWith('\t')
                ? raw
                : IndentRawContent(raw);
        }

        if (newBodyBlock is not null)
            SetOrInsertAfter(blocks, activeBodyBlockName, newBodyBlock, bruMethod);

        // Also update non-active text body blocks that have tracked content.
        // These come from AllBodyContents, which the ViewModel keeps in sync as the user
        // switches between body types, so edits to a body type that wasn't active at save
        // time are not silently discarded.
        var inactiveTextTypes = new[]
        {
            (CollectionRequest.BodyTypes.Json, "body:json"),
            (CollectionRequest.BodyTypes.Text, "body:text"),
            (CollectionRequest.BodyTypes.Xml,  "body:xml"),
        };
        foreach (var (type, blockName) in inactiveTextTypes)
        {
            if (type == request.BodyType) continue; // already handled as active block above
            if (!request.AllBodyContents.TryGetValue(type, out var inactiveContent)) continue;

            var inactiveBlock = new BruBlock(blockName);
            inactiveBlock.RawContent = inactiveContent.StartsWith(' ') || inactiveContent.StartsWith('\t')
                ? inactiveContent
                : IndentRawContent(inactiveContent);
            SetOrInsertAfter(blocks, blockName, inactiveBlock, bruMethod);
        }
    }

    /// <summary>
    /// Updates or removes auth blocks (<c>auth:bearer</c> / <c>auth:basic</c>) in-place.
    /// </summary>
    private static void UpdateAuthBlockInPlace(
        List<BruBlock> blocks, CollectionRequest request, BruDocument? existing, string bruMethod)
    {
        // Remove both auth blocks first; re-add the one that's active below.
        // (We replace in-place when possible to preserve position.)
        switch (request.Auth.AuthType)
        {
            case AuthConfig.AuthTypes.Bearer:
                RemoveBlock(blocks, "auth:basic");
                var bearerBlock = new BruBlock("auth:bearer");
                bearerBlock.Items.Add(new BruKv("token", request.Auth.Token ?? string.Empty));
                SetOrInsertAfter(blocks, "auth:bearer", bearerBlock, bruMethod);
                break;

            case AuthConfig.AuthTypes.Basic:
                RemoveBlock(blocks, "auth:bearer");
                var basicBlock = new BruBlock("auth:basic");
                basicBlock.Items.Add(new BruKv("username", request.Auth.Username ?? string.Empty));
                // Password is stored in local secret storage — never write a literal value to the
                // file.  However, if the existing file had a {{variable}} reference, preserve it
                // so Bruno users whose password is stored as an env-var are not broken.
                var existingPw = existing?.GetValue("auth:basic", "password") ?? string.Empty;
                var pwToWrite = IsEnvVarRef(existingPw) ? existingPw : string.Empty;
                basicBlock.Items.Add(new BruKv("password", pwToWrite));
                SetOrInsertAfter(blocks, "auth:basic", basicBlock, bruMethod);
                break;

            default:
                RemoveBlock(blocks, "auth:bearer");
                RemoveBlock(blocks, "auth:basic");
                break;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> is a Bruno environment-variable
    /// reference such as <c>{{myVar}}</c>.  These should never be erased on save.
    /// </summary>
    private static bool IsEnvVarRef(string value) =>
        value.StartsWith("{{" , StringComparison.Ordinal) &&
        value.EndsWith("}}", StringComparison.Ordinal);

    private static string IndentRawContent(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        return string.Join('\n', content.Split('\n').Select(l => "  " + l.TrimEnd('\r')));
    }

    private static string BuildNewRequestContent(string name, int seq)
    {
        var blocks = new List<BruBlock>();

        var meta = new BruBlock("meta");
        meta.Items.Add(new BruKv("name", name));
        meta.Items.Add(new BruKv("type", "http"));
        meta.Items.Add(new BruKv("seq", seq.ToString()));
        // requestId is intentionally omitted — Bruno does not use this field.
        blocks.Add(meta);

        var get = new BruBlock("get");
        get.Items.Add(new BruKv("url", string.Empty));
        get.Items.Add(new BruKv("body", "none"));
        get.Items.Add(new BruKv("auth", "none"));
        blocks.Add(get);

        return BruWriter.Write(blocks);
    }

    private static int ComputeNextSeq(string folderPath)
    {
        var maxSeq = Directory
            .EnumerateFiles(folderPath, "*.bru", SearchOption.TopDirectoryOnly)
            .Select(f =>
            {
                try
                {
                    var text = File.ReadAllText(f);
                    var doc = BruParser.Parse(text);
                    var seqStr = doc.GetValue("meta", "seq");
                    return int.TryParse(seqStr, out var s) ? s : 0;
                }
                catch { return 0; }
            })
            .DefaultIfEmpty(0)
            .Max();
        return maxSeq + 1;
    }

    private async Task UpdateSeqInFileAsync(string filePath, int seq, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var doc = BruParser.Parse(text);
        var meta = doc.Find("meta");
        if (meta is null) return;

        var idx = meta.Items.FindIndex(
            kv => string.Equals(kv.Key, "seq", StringComparison.OrdinalIgnoreCase));
        var seqKv = new BruKv("seq", seq.ToString());

        if (idx >= 0)
            meta.Items[idx] = seqKv;
        else
            meta.Items.Add(seqKv);

        await File.WriteAllTextAsync(filePath, BruWriter.Write(doc.Blocks, doc.LineEnding), ct).ConfigureAwait(false);
    }

    private static void SetMetaName(BruDocument doc, string name)
    {
        var meta = doc.Find("meta");
        if (meta is null) return;

        var idx = meta.Items.FindIndex(
            kv => string.Equals(kv.Key, "name", StringComparison.OrdinalIgnoreCase));
        var nameKv = new BruKv("name", name);

        if (idx >= 0)
            meta.Items[idx] = nameKv;
        else
            meta.Items.Insert(0, nameKv);
    }

    /// <summary>
    /// Computes a stable request identity for Bruno requests based on their display path
    /// (folder path + meta.name). This identity is stable within the collection but is lost
    /// on rename or move operations, as per Issue 40 requirements for Bruno compatibility.
    /// The identity is a deterministic GUID derived from the display path using SHA256 hashing.
    /// </summary>
    internal static Guid ComputeBrunoRequestIdentity(string collectionRootPath, string filePath, string requestName)
    {
        // Compute display path: relative folder path + request name
        var relativePath = Path.GetRelativePath(collectionRootPath, filePath);
        var folderPath = Path.GetDirectoryName(relativePath) ?? string.Empty;
        
        // Normalize to forward slashes for consistency across platforms
        var displayPath = folderPath.Replace('\\', '/');
        if (!string.IsNullOrEmpty(displayPath) && !displayPath.EndsWith('/'))
            displayPath += '/';
        displayPath += requestName;

        // Create a deterministic GUID from the display path using SHA256
        return GuidFromString(displayPath);
    }

    /// <summary>
    /// Creates a deterministic GUID from a string using SHA256 hashing.
    /// Used to compute stable Bruno request identities.
    /// </summary>
    private static Guid GuidFromString(string value)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
            // Take the first 16 bytes and convert to Guid
            return new Guid(hash.Take(16).ToArray());
        }
    }

    private static bool TryGetRequestId(BruDocument doc, out Guid requestId)
    {
        var raw = doc.GetValue("meta", "requestId");
        return Guid.TryParse(raw, out requestId);
    }

    /// <summary>
    /// Returns the key used to store Basic auth secrets in <see cref="ISecretStorageService"/>.
    /// The key is the file path relative to the collection root (forward-slash separated) so
    /// that it is stable within a collection but changes when the file is renamed or moved —
    /// which is intentional: history and secrets are expected to be lost on rename/move for
    /// Bruno collections, and this avoids writing a foreign <c>requestId</c> field into the
    /// <c>.bru</c> file.
    /// </summary>
    private string GetAuthSecretKey(string filePath)
    {
        if (string.IsNullOrEmpty(_currentRoot))
            return Path.GetFileName(filePath);
        return Path.GetRelativePath(_currentRoot, filePath).Replace('\\', '/');
    }

    /// <summary>
    /// Moves the Basic auth secret stored under <paramref name="oldFilePath"/>'s key to the
    /// key for <paramref name="newFilePath"/>. No-op when the two keys are equal or no secret
    /// exists.
    /// </summary>
    private async Task MigrateAuthSecretAsync(string oldFilePath, string newFilePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentRoot)) return;
        var oldKey = GetAuthSecretKey(oldFilePath);
        var newKey = GetAuthSecretKey(newFilePath);
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;
        var secret = await _secrets.GetSecretAsync(_currentRoot, AuthSecretsNamespace, oldKey, ct).ConfigureAwait(false);
        if (secret is null) return;
        await _secrets.SetSecretAsync(_currentRoot, AuthSecretsNamespace, newKey, secret, ct).ConfigureAwait(false);
        await _secrets.DeleteSecretAsync(_currentRoot, AuthSecretsNamespace, oldKey, ct).ConfigureAwait(false);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join('_', name.Split(invalid, StringSplitOptions.None));
    }
}
