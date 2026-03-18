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
/// On every save the existing file (if present) is re-read to extract script blocks
/// (<c>script:pre-request</c>, <c>script:post-response</c>, <c>tests</c>) and all
/// disabled KV items (<c>~</c>-prefixed) so they survive Callsmith edits unchanged.
/// </para>
/// </summary>
public sealed class BrunoCollectionService : ICollectionService
{
    public const string RequestFileExtension = ".bru";
    string ICollectionService.RequestFileExtension => RequestFileExtension;

    /// <summary>Environment sub-folder name used by Bruno (plural, unlike Callsmith's singular).</summary>
    public const string EnvironmentFolderName = "environments";

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
            // Prefer the secret-stored value; fall back to whatever is in the file (migration path).
            var stored = await _secrets
                .GetSecretAsync(_currentRoot, AuthSecretsNamespace, RequestKey(filePath), ct)
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
        var newContent = BruWriter.Write(doc.Blocks);
        await File.WriteAllTextAsync(newFilePath, newContent, ct).ConfigureAwait(false);

        if (!string.Equals(filePath, newFilePath, StringComparison.OrdinalIgnoreCase))
            File.Delete(filePath);

        _logger.LogDebug("Renamed Bruno request '{OldPath}' → '{NewPath}'", filePath, newFilePath);

        var existing = await LoadRequestAsync(newFilePath, ct).ConfigureAwait(false);
        return existing;
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

        return new CollectionRequest
        {
            FilePath = filePath,
            Name = name,
            Method = HttpMethod.Get,
            Url = string.Empty,
            Headers = [],
            PathParams = new Dictionary<string, string>(),
            QueryParams = [],
            BodyType = CollectionRequest.BodyTypes.None,
            Auth = new AuthConfig(),
        };
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
                await File.WriteAllTextAsync(folderBruPath, BruWriter.Write(doc.Blocks), ct).ConfigureAwait(false);
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
        Directory.Delete(folderPath, recursive: true);
        _logger.LogDebug("Deleted Bruno folder '{FolderPath}'", folderPath);
        return Task.CompletedTask;
    }

    public async Task SaveFolderOrderAsync(
        string folderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(orderedNames);

        // Assign seq values to .bru request files based on their position.
        // Folder entries (no extension) count toward position so inter-mixed ordering is preserved.
        var seq = 1;
        foreach (var name in orderedNames)
        {
            if (name.EndsWith(RequestFileExtension, StringComparison.OrdinalIgnoreCase))
            {
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
                var seqVal = int.TryParse(seqStr, out var s) ? s : int.MaxValue;
                parsedRequests.Add((req, seqVal));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable .bru file: '{Path}'", filePath);
            }
        }

        var sortedRequests = parsedRequests
            .OrderBy(r => r.Seq)
            .ThenBy(r => r.Request.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Request)
            .ToList();

        var subFolders = Directory
            .EnumerateDirectories(folderPath)
            .Where(d => !string.Equals(
                Path.GetFileName(d), EnvironmentFolderName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Select(d => ReadFolder(d))
            .ToList();

        // ItemOrder: sub-folders first (alphabetical, matching Bruno's default), then requests by seq.
        // This lets CollectionTreeItemViewModel.FromFolder honour the full mixed ordering.
        var itemOrder = subFolders
            .Select(f => f.Name)
            .Concat(sortedRequests.Select(r => Path.GetFileName(r.FilePath)))
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

    // ─────────────────────────────────────────────────────────────────────────
    //  Private — BruDocument ↔ CollectionRequest mapping
    // ─────────────────────────────────────────────────────────────────────────

    private static CollectionRequest? DocToRequest(BruDocument doc, string filePath, string? basicPasswordOverride = null)
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
        string baseUrl;
        IReadOnlyList<RequestKv> queryParams;
        var queryBlock = doc.Find("params:query");
        if (queryBlock?.Items.Count > 0)
        {
            baseUrl = QueryStringHelper.GetBaseUrl(rawUrl);
            // Preserve all items (enabled and disabled) in the domain model.
            queryParams = queryBlock.Items
                .Select(kv => new RequestKv(kv.Key, kv.Value, kv.IsEnabled))
                .ToList();
        }
        else
        {
            // Fall back to parsing from the URL itself (e.g. no params:query block).
            baseUrl = QueryStringHelper.GetBaseUrl(rawUrl);
            queryParams = QueryStringHelper.ParseQueryParams(rawUrl)
                .Select(kv => new RequestKv(kv.Key, kv.Value))
                .ToList();
        }

        // Preserve all header items (enabled and disabled).
        var headers = doc.Find("headers")?.Items
            .Select(kv => new RequestKv(kv.Key, kv.Value, kv.IsEnabled))
            .ToList() ?? (IReadOnlyList<RequestKv>)[];

        var bodyType = MapBrunoBodyType(bruBodyType);
        var body = BuildBody(doc, bodyType);
        var formParams = BuildFormParams(doc, bodyType);
        var authType = MapBrunoAuthType(bruAuthType);
        var auth = BuildAuth(doc, authType, basicPasswordOverride);

        return new CollectionRequest
        {
            FilePath = filePath,
            Name = name,
            Method = new HttpMethod(httpMethod),
            Url = baseUrl,
            Headers = headers,
            PathParams = new Dictionary<string, string>(),
            QueryParams = queryParams,
            BodyType = bodyType,
            Body = body,
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
        var blocks = new List<BruBlock>();

        // meta
        var meta = new BruBlock("meta");
        meta.Items.Add(new BruKv("name", request.Name));
        meta.Items.Add(new BruKv("type", "http"));
        meta.Items.Add(new BruKv("seq", existing?.GetValue("meta", "seq") ?? "1"));
        blocks.Add(meta);

        // HTTP method block
        var bruMethod = request.Method.Method.ToLowerInvariant();
        var method = new BruBlock(bruMethod);
        // Write the full URL (with query params) so Bruno's address bar shows the complete URL.
        method.Items.Add(new BruKv("url", request.FullUrl));
        method.Items.Add(new BruKv("body", MapCallsmithBodyType(request.BodyType)));
        method.Items.Add(new BruKv("auth", MapCallsmithAuthType(request.Auth.AuthType)));
        blocks.Add(method);

        // params:query — write all items (enabled and disabled) with their state.
        if (request.QueryParams.Count > 0)
        {
            var queryBlock = new BruBlock("params:query");
            foreach (var p in request.QueryParams)
                queryBlock.Items.Add(new BruKv(p.Key, p.Value, p.IsEnabled));
            blocks.Add(queryBlock);
        }

        // headers — write all items (enabled and disabled) with their state.
        if (request.Headers.Count > 0)
        {
            var headersBlock = new BruBlock("headers");
            foreach (var h in request.Headers)
                headersBlock.Items.Add(new BruKv(h.Key, h.Value, h.IsEnabled));
            blocks.Add(headersBlock);
        }

        // body
        AddBodyBlock(blocks, request);

        // auth
        AddAuthBlock(blocks, request, existing);

        // Preserved blocks: scripts, tests, and path params are written back unchanged.
        if (existing is not null)
        {
            foreach (var block in existing.Blocks)
            {
                if (IsPreservedBlockName(block.Name))
                    blocks.Add(block);
            }
        }

        return BruWriter.Write(blocks);
    }

    private static void AddBodyBlock(List<BruBlock> blocks, CollectionRequest request)
    {
        if (request.BodyType is CollectionRequest.BodyTypes.Form or CollectionRequest.BodyTypes.Multipart)
        {
            if (request.FormParams.Count == 0) return;

            var blockName = request.BodyType == CollectionRequest.BodyTypes.Form
                ? "body:form-urlencoded"
                : "body:multipart";
            var block = new BruBlock(blockName);
            foreach (var (k, v) in request.FormParams)
                block.Items.Add(new BruKv(k, v));
            blocks.Add(block);
            return;
        }

        if (request.Body is null) return;

        var bodyBlockName = request.BodyType switch
        {
            CollectionRequest.BodyTypes.Json => "body:json",
            CollectionRequest.BodyTypes.Text => "body:text",
            CollectionRequest.BodyTypes.Xml => "body:xml",
            _ => "body:json",
        };

        var rawBlock = new BruBlock(bodyBlockName);
        var raw = request.Body;
        rawBlock.RawContent = raw.StartsWith(' ') || raw.StartsWith('\t')
            ? raw
            : IndentRawContent(raw);
        blocks.Add(rawBlock);
    }

    private static void AddAuthBlock(List<BruBlock> blocks, CollectionRequest request, BruDocument? existing)
    {
        switch (request.Auth.AuthType)
        {
            case AuthConfig.AuthTypes.Bearer:
                var bearerBlock = new BruBlock("auth:bearer");
                bearerBlock.Items.Add(new BruKv("token", request.Auth.Token ?? string.Empty));
                blocks.Add(bearerBlock);
                break;

            case AuthConfig.AuthTypes.Basic:
                var basicBlock = new BruBlock("auth:basic");
                basicBlock.Items.Add(new BruKv("username", request.Auth.Username ?? string.Empty));
                // Password is stored in local secret storage — never write a literal value to the
                // file.  However, if the existing file had a {{variable}} reference, preserve it
                // so Bruno users whose password is stored as an env-var are not broken.
                var existingPw = existing?.GetValue("auth:basic", "password") ?? string.Empty;
                var pwToWrite = IsEnvVarRef(existingPw) ? existingPw : string.Empty;
                basicBlock.Items.Add(new BruKv("password", pwToWrite));
                blocks.Add(basicBlock);
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

    /// <summary>
    /// Returns a stable, normalised key for a request file relative to the current collection root.
    /// Used to key Basic auth passwords in local secret storage.
    /// </summary>
    private string RequestKey(string filePath) =>
        Path.GetRelativePath(_currentRoot, Path.GetFullPath(filePath)).Replace('\\', '/');

    private static bool IsPreservedBlockName(string name) =>
        name is "script:pre-request" or "script:post-response" or "tests" or "params:path";

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

        await File.WriteAllTextAsync(filePath, BruWriter.Write(doc.Blocks), ct).ConfigureAwait(false);
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

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join('_', name.Split(invalid, StringSplitOptions.None));
    }
}
