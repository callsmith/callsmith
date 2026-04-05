using Callsmith.Core.Abstractions;
using Callsmith.Core.Import;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// Orchestrates the full import pipeline: detects the format, parses the file, then
/// writes all requests, folders, and environments to disk using
/// <see cref="ICollectionService"/> and <see cref="IEnvironmentService"/>.
/// </summary>
public sealed class CollectionImportService : ICollectionImportService
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly IReadOnlyList<ICollectionImporter> _importers;
    private readonly ICollectionService _collectionService;
    private readonly IEnvironmentService _environmentService;
    private readonly ILogger<CollectionImportService> _logger;

    public CollectionImportService(
        IEnumerable<ICollectionImporter> importers,
        ICollectionService collectionService,
        IEnvironmentService environmentService,
        ILogger<CollectionImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(environmentService);
        ArgumentNullException.ThrowIfNull(logger);

        _importers = [.. importers];
        _collectionService = collectionService;
        _environmentService = environmentService;
        _logger = logger;

        SupportedFileExtensions = _importers
            .SelectMany(i => i.SupportedFileExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedFileExtensions { get; }

    /// <inheritdoc/>
    public async Task<ICollectionImporter?> FindImporterAsync(
        string filePath, CancellationToken ct = default)
    {
        foreach (var importer in _importers)
        {
            if (await importer.CanImportAsync(filePath, ct).ConfigureAwait(false))
                return importer;
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task<ImportedCollection> ImportToFolderAsync(
        string filePath,
        string targetFolderPath,
        CancellationToken ct = default)
    {
        var importer = await FindImporterAsync(filePath, ct).ConfigureAwait(false);
        if (importer is null)
            throw new InvalidOperationException(
                $"No importer found for file '{filePath}'. Supported formats: " +
                string.Join(", ", _importers.Select(i => i.FormatName)));

        _logger.LogInformation(
            "Importing '{FilePath}' using {Format} importer into '{TargetFolder}'",
            filePath, importer.FormatName, targetFolderPath);

        var collection = await importer.ImportAsync(filePath, ct).ConfigureAwait(false);

        Directory.CreateDirectory(targetFolderPath);

        // Ensure the collection service's current-root context is set before writing
        // any requests, so that auth secrets (e.g. Basic auth passwords) are persisted
        // to local secret storage rather than silently dropped.
        await _collectionService.OpenFolderAsync(targetFolderPath, ct).ConfigureAwait(false);

        // Write root requests and track original→actual name mapping for order file.
        var rootNameQueues = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach (var req in collection.RootRequests)
        {
            var actualName = await WriteRequestAsync(req, targetFolderPath, ct).ConfigureAwait(false);
            EnqueueName(rootNameQueues, req.Name, actualName);
        }

        // Write root folders (recursive)
        foreach (var folder in collection.RootFolders)
            await WriteFolderAsync(folder, targetFolderPath, ct).ConfigureAwait(false);

        // Persist the interleaved sort order from the source tool.
        await WriteFolderOrderAsync(targetFolderPath, collection.ItemOrder, rootNameQueues, ct).ConfigureAwait(false);

        // Write environments and record their filenames in import order so the
        // prefs-based ordering mechanism can restore the original order on first load.
        var envFileOrder = new List<string>();
        foreach (var env in collection.Environments)
        {
            var fileName = await WriteEnvironmentAsync(env, targetFolderPath, ct).ConfigureAwait(false);
            envFileOrder.Add(fileName);
        }

        if (envFileOrder.Count > 0)
            await _environmentService.SaveEnvironmentOrderAsync(targetFolderPath, envFileOrder, ct).ConfigureAwait(false);

        // Merge global dynamic vars (extracted from inline request-field tags) into the
        // global environment, deduplicating by variable name.
        if (collection.GlobalDynamicVars.Count > 0)
            await MergeGlobalDynamicVarsAsync(collection.GlobalDynamicVars, targetFolderPath, ct)
                .ConfigureAwait(false);

        _logger.LogInformation(
            "Import complete: {RequestCount} root requests, {FolderCount} folders, {EnvCount} environments",
            collection.RootRequests.Count,
            collection.RootFolders.Count,
            collection.Environments.Count);

        return collection;
    }

    /// <inheritdoc/>
    public async Task<ImportedCollection> ImportIntoCollectionAsync(
        string filePath,
        string collectionRootPath,
        string? targetSubFolderPath = null,
        CancellationToken ct = default)
    {
        var importer = await FindImporterAsync(filePath, ct).ConfigureAwait(false);
        if (importer is null)
            throw new InvalidOperationException(
                $"No importer found for file '{filePath}'. Supported formats: " +
                string.Join(", ", _importers.Select(i => i.FormatName)));

        // Normalise the target: null or same as root → requests land at root.
        // Use platform-appropriate path comparison: case-insensitive on Windows,
        // case-sensitive on Linux/macOS.
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var effectiveTarget = string.IsNullOrEmpty(targetSubFolderPath) ||
                              string.Equals(targetSubFolderPath, collectionRootPath, pathComparison)
            ? collectionRootPath
            : targetSubFolderPath;

        _logger.LogInformation(
            "Importing '{FilePath}' using {Format} importer into existing collection '{CollectionRoot}' (target folder: '{TargetFolder}')",
            filePath, importer.FormatName, collectionRootPath, effectiveTarget);

        var collection = await importer.ImportAsync(filePath, ct).ConfigureAwait(false);

        // Ensure the target sub-folder exists (the collection root already exists).
        Directory.CreateDirectory(effectiveTarget);

        // Write root requests at the target folder; deduplication is handled by WriteRequestAsync.
        var rootNameQueues = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach (var req in collection.RootRequests)
        {
            var actualName = await WriteRequestAsync(req, effectiveTarget, ct).ConfigureAwait(false);
            EnqueueName(rootNameQueues, req.Name, actualName);
        }

        // Write root folders (recursive).
        foreach (var folder in collection.RootFolders)
            await WriteFolderAsync(folder, effectiveTarget, ct).ConfigureAwait(false);

        // Persist the interleaved sort order from the source tool.
        await WriteFolderOrderAsync(effectiveTarget, collection.ItemOrder, rootNameQueues, ct).ConfigureAwait(false);

        // Merge environments by name into the existing collection environments.
        if (collection.Environments.Count > 0)
            await MergeEnvironmentsIntoCollectionAsync(collection.Environments, collectionRootPath, ct)
                .ConfigureAwait(false);

        // Merge global dynamic vars using additive-only semantics (same as ImportToFolderAsync).
        if (collection.GlobalDynamicVars.Count > 0)
            await MergeGlobalDynamicVarsAsync(collection.GlobalDynamicVars, collectionRootPath, ct)
                .ConfigureAwait(false);

        _logger.LogInformation(
            "Merge-import complete: {RequestCount} root requests, {FolderCount} folders, {EnvCount} environments",
            collection.RootRequests.Count,
            collection.RootFolders.Count,
            collection.Environments.Count);

        return collection;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task WriteFolderAsync(
        ImportedFolder folder, string parentPath, CancellationToken ct)
    {
        var folderPath = Path.Combine(parentPath, SanitizeName(folder.Name));
        Directory.CreateDirectory(folderPath);

        var nameQueues = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach (var req in folder.Requests)
        {
            var actualName = await WriteRequestAsync(req, folderPath, ct).ConfigureAwait(false);
            EnqueueName(nameQueues, req.Name, actualName);
        }

        foreach (var sub in folder.SubFolders)
            await WriteFolderAsync(sub, folderPath, ct).ConfigureAwait(false);

        await WriteFolderOrderAsync(folderPath, folder.ItemOrder, nameQueues, ct).ConfigureAwait(false);
    }

    /// <returns>The base name (without extension) of the file written on disk, which may
    /// differ from <paramref name="imported"/>.<see cref="ImportedRequest.Name"/> when a
    /// duplicate was detected and a counter suffix was appended.</returns>
    private async Task<string> WriteRequestAsync(
        ImportedRequest imported, string folderPath, CancellationToken ct)
    {
        var safeName = SanitizeName(imported.Name);
        var filePath = Path.Combine(
            folderPath,
            safeName + _collectionService.RequestFileExtension);

        // Deduplicate: append a counter when the filename is already taken
        var counter = 1;
        while (File.Exists(filePath))
        {
            filePath = Path.Combine(
                folderPath,
                $"{safeName} ({counter}){_collectionService.RequestFileExtension}");
            counter++;
        }

        var request = new CollectionRequest
        {
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath),
            Method = imported.Method,
            Url = imported.Url,
            Description = imported.Description,
            Headers = imported.Headers,
            BodyType = imported.BodyType,
            Body = imported.Body,
            FormParams = imported.FormParams,
            PathParams = imported.PathParams,
            QueryParams = imported.QueryParams,
            Auth = imported.Auth,
        };

        await _collectionService.SaveRequestAsync(request, ct).ConfigureAwait(false);
        _logger.LogDebug("Wrote request '{Name}' → '{FilePath}'", request.Name, filePath);
        return request.Name;
    }

    /// <returns>The basename of the written file (e.g. <c>Dev.env.callsmith</c>).</returns>
    private async Task<string> WriteEnvironmentAsync(
        ImportedEnvironment imported, string collectionFolderPath, CancellationToken ct)
    {
        // Static variables
        var variables = imported.Variables
            .Select(kv => new EnvironmentVariable
            {
                Name = kv.Key,
                Value = kv.Value,
                VariableType = EnvironmentVariable.VariableTypes.Static,
                IsSecret = false,
            })
            .ToList();

        // Dynamic variables (typed: mock-data or response-body)
        foreach (var dv in imported.DynamicVariables)
        {
            EnvironmentVariable env;
            if (dv.IsMockData)
            {
                env = new EnvironmentVariable
                {
                    Name = dv.Name,
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.MockData,
                    IsSecret = false,
                    MockDataCategory = dv.MockDataCategory,
                    MockDataField = dv.MockDataField,
                };
            }
            else
            {
                env = new EnvironmentVariable
                {
                    Name = dv.Name,
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                    IsSecret = false,
                    ResponseRequestName = dv.ResponseRequestName,
                    ResponsePath = dv.ResponsePath,
                    ResponseMatcher = dv.ResponseMatcher,
                    ResponseFrequency = dv.ResponseFrequency,
                    ResponseExpiresAfterSeconds = dv.ResponseExpiresAfterSeconds,
                };
            }
            variables.Add(env);
        }

        var envFolder = Path.Combine(
            collectionFolderPath, FileSystemCollectionService.EnvironmentFolderName);
        Directory.CreateDirectory(envFolder);

        var safeName = SanitizeName(imported.Name);
        var filePath = Path.Combine(
            envFolder,
            safeName + FileSystemEnvironmentService.EnvironmentFileExtension);

        // Deduplicate
        var counter = 1;
        while (File.Exists(filePath))
        {
            filePath = Path.Combine(
                envFolder,
                $"{safeName} ({counter}){FileSystemEnvironmentService.EnvironmentFileExtension}");
            counter++;
        }

        var model = new EnvironmentModel
        {
            FilePath = filePath,
            Name = imported.Name,
            Variables = variables,
            Color = imported.Color,
            EnvironmentId = Guid.NewGuid(),
        };

        await _environmentService.SaveEnvironmentAsync(model, ct).ConfigureAwait(false);
        _logger.LogDebug("Wrote environment '{Name}' → '{FilePath}'", model.Name, filePath);
        return Path.GetFileName(filePath);
    }

    private static string SanitizeName(string name)
    {
        var sanitized = string.Join('_', name.Split(InvalidFileNameChars, StringSplitOptions.None));
        return string.IsNullOrWhiteSpace(sanitized) ? "Unnamed" : sanitized;
    }

    /// <summary>
    /// Merges <paramref name="globalVars"/> into the collection's global environment,
    /// skipping any var whose name already exists there.
    /// </summary>
    private async Task MergeGlobalDynamicVarsAsync(
        IReadOnlyList<ImportedDynamicVariable> globalVars,
        string collectionFolderPath,
        CancellationToken ct)
    {
        var globalModel = await _environmentService
            .LoadGlobalEnvironmentAsync(collectionFolderPath, ct)
            .ConfigureAwait(false);

        var existing = new HashSet<string>(
            globalModel.Variables.Select(v => v.Name), StringComparer.Ordinal);

        var toAdd = new List<EnvironmentVariable>();
        foreach (var dv in globalVars)
        {
            if (existing.Contains(dv.Name)) continue;
            existing.Add(dv.Name);

            toAdd.Add(dv.IsMockData
                ? new EnvironmentVariable
                {
                    Name = dv.Name,
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.MockData,
                    MockDataCategory = dv.MockDataCategory,
                    MockDataField = dv.MockDataField,
                }
                : new EnvironmentVariable
                {
                    Name = dv.Name,
                    Value = string.Empty,
                    VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                    ResponseRequestName = dv.ResponseRequestName,
                    ResponsePath = dv.ResponsePath,
                    ResponseMatcher = dv.ResponseMatcher,
                    ResponseFrequency = dv.ResponseFrequency,
                    ResponseExpiresAfterSeconds = dv.ResponseExpiresAfterSeconds,
                });
        }

        if (toAdd.Count == 0) return;

        var updated = globalModel with
        {
            Variables = [.. globalModel.Variables, .. toAdd],
        };
        await _environmentService.SaveGlobalEnvironmentAsync(updated, ct).ConfigureAwait(false);
        _logger.LogDebug(
            "Added {Count} global dynamic variable(s) to global environment", toAdd.Count);
    }

    /// <summary>
    /// Merges <paramref name="importedEnvironments"/> into an existing collection:
    /// <list type="bullet">
    ///   <item>If an environment with the same name already exists, new variables
    ///   from the import are appended; existing variables are never changed or
    ///   removed.</item>
    ///   <item>If no matching environment exists, the environment is added in full.</item>
    /// </list>
    /// </summary>
    private async Task MergeEnvironmentsIntoCollectionAsync(
        IReadOnlyList<ImportedEnvironment> importedEnvironments,
        string collectionRootPath,
        CancellationToken ct)
    {
        var existingEnvs = await _environmentService
            .ListEnvironmentsAsync(collectionRootPath, ct)
            .ConfigureAwait(false);

        // Build a lookup: env name → model (case-sensitive, matching existing behaviour).
        var envByName = existingEnvs.ToDictionary(e => e.Name, StringComparer.Ordinal);

        // Track newly created environment filenames so we can append them to the order file.
        var newEnvFileNames = new List<string>();

        foreach (var imported in importedEnvironments)
        {
            if (envByName.TryGetValue(imported.Name, out var existing))
            {
                // ── Merge into existing environment ──────────────────────────
                var existingNames = new HashSet<string>(
                    existing.Variables.Select(v => v.Name), StringComparer.Ordinal);

                var toAdd = new List<EnvironmentVariable>();

                // Static variables present in the import but absent from the existing env.
                foreach (var kv in imported.Variables)
                {
                    if (existingNames.Contains(kv.Key)) continue;
                    existingNames.Add(kv.Key);
                    toAdd.Add(new EnvironmentVariable
                    {
                        Name = kv.Key,
                        Value = kv.Value,
                        VariableType = EnvironmentVariable.VariableTypes.Static,
                        IsSecret = false,
                    });
                }

                // Dynamic variables present in the import but absent from the existing env.
                foreach (var dv in imported.DynamicVariables)
                {
                    if (existingNames.Contains(dv.Name)) continue;
                    existingNames.Add(dv.Name);

                    toAdd.Add(dv.IsMockData
                        ? new EnvironmentVariable
                        {
                            Name = dv.Name,
                            Value = string.Empty,
                            VariableType = EnvironmentVariable.VariableTypes.MockData,
                            MockDataCategory = dv.MockDataCategory,
                            MockDataField = dv.MockDataField,
                        }
                        : new EnvironmentVariable
                        {
                            Name = dv.Name,
                            Value = string.Empty,
                            VariableType = EnvironmentVariable.VariableTypes.ResponseBody,
                            ResponseRequestName = dv.ResponseRequestName,
                            ResponsePath = dv.ResponsePath,
                            ResponseMatcher = dv.ResponseMatcher,
                            ResponseFrequency = dv.ResponseFrequency,
                            ResponseExpiresAfterSeconds = dv.ResponseExpiresAfterSeconds,
                        });
                }

                if (toAdd.Count == 0)
                {
                    _logger.LogDebug(
                        "Environment '{Name}' already exists; no new variables to merge", imported.Name);
                    continue;
                }

                var updated = existing with
                {
                    Variables = [.. existing.Variables, .. toAdd],
                };
                await _environmentService.SaveEnvironmentAsync(updated, ct).ConfigureAwait(false);
                _logger.LogDebug(
                    "Merged {Count} new variable(s) into existing environment '{Name}'",
                    toAdd.Count, imported.Name);
            }
            else
            {
                // ── Add as a new environment ──────────────────────────────────
                var fileName = await WriteEnvironmentAsync(imported, collectionRootPath, ct).ConfigureAwait(false);
                newEnvFileNames.Add(fileName);
                _logger.LogDebug(
                    "Added new environment '{Name}' → '{FileName}'", imported.Name, fileName);
            }
        }

        // Append newly added environment files to the end of the existing order list.
        if (newEnvFileNames.Count > 0)
        {
            var envFolder = Path.Combine(
                collectionRootPath, FileSystemCollectionService.EnvironmentFolderName);
            var orderFile = Path.Combine(envFolder, FileSystemEnvironmentService.OrderFileName);

            List<string> existingOrder = [];
            if (File.Exists(orderFile))
            {
                var json = await File.ReadAllTextAsync(orderFile, ct).ConfigureAwait(false);
                existingOrder = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }

            await _environmentService.SaveEnvironmentOrderAsync(
                collectionRootPath,
                [.. existingOrder, .. newEnvFileNames],
                ct).ConfigureAwait(false);
        }
    }

    private static void EnqueueName(
        Dictionary<string, Queue<string>> queues, string originalName, string actualName)
    {
        if (!queues.TryGetValue(originalName, out var q))
        {
            q = new Queue<string>();
            queues[originalName] = q;
        }
        q.Enqueue(actualName);
    }

    /// <summary>
    /// Translates the importer's raw name list into the on-disk format expected by
    /// <see cref="FileSystemCollectionService.SaveFolderOrderAsync"/>.
    /// Requests use the actual (possibly deduplicated) on-disk name with the file extension;
    /// sub-folder names are kept as-is.
    /// Only writes when the order list is non-empty.
    /// </summary>
    private async Task WriteFolderOrderAsync(
        string folderPath,
        IReadOnlyList<string> itemOrder,
        IReadOnlyDictionary<string, Queue<string>> requestNameQueues,
        CancellationToken ct)
    {
        if (itemOrder.Count == 0) return;

        var ext = _collectionService.RequestFileExtension;
        var onDiskOrder = itemOrder.Select(name =>
        {
            // Use the queued actual name (handles renames and duplicate-counter suffixes).
            if (requestNameQueues.TryGetValue(name, out var q) && q.Count > 0)
                return q.Dequeue() + ext;

            // Fallback: if a request file with the original name exists, add the extension;
            // otherwise treat the entry as a sub-folder name.
            var requestFile = Path.Combine(folderPath, name + ext);
            return File.Exists(requestFile) ? name + ext : name;
        }).ToList();

        await _collectionService.SaveFolderOrderAsync(folderPath, onDiskOrder, ct)
            .ConfigureAwait(false);
    }
}
