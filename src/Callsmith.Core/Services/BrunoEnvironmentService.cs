using Callsmith.Core.Abstractions;
using Callsmith.Core.Bruno;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;
using static Callsmith.Core.Models.EnvironmentVariable;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="IEnvironmentService"/> implementation that reads and writes Bruno environment
/// files located in the <c>environments/</c> sub-folder of a Bruno collection.
/// <para>
/// Each environment is a <c>&lt;Name&gt;.bru</c> file containing one or more of:
/// <list type="bullet">
///   <item><c>vars { key: value }</c> — regular (non-secret) variables</item>
///   <item><c>vars:secret [ name ]</c> — secret variable names only; actual values stay in local storage</item>
/// </list>
/// </para>
/// </summary>
public sealed class BrunoEnvironmentService : IEnvironmentService
{
    public const string EnvironmentFolderName = "environments";
    public const string EnvironmentFileExtension = ".bru";
    public const string GlobalEnvironmentFileName = "_global";

    private readonly ISecretStorageService _secrets;
    private readonly IBrunoCollectionMetaService _meta;
    private readonly ILogger<BrunoEnvironmentService> _logger;

    public BrunoEnvironmentService(
        ISecretStorageService secrets,
        IBrunoCollectionMetaService meta,
        ILogger<BrunoEnvironmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = secrets;
        _meta = meta;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  List / Load
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EnvironmentModel>> ListEnvironmentsAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        var envFolder = GetEnvFolder(collectionFolderPath);
        if (!Directory.Exists(envFolder)) return [];

        // Load meta once to apply ordering and colors without per-file overhead.
        var meta = await _meta.LoadAsync(collectionFolderPath, ct).ConfigureAwait(false);

        var results = new List<EnvironmentModel>();
        foreach (var filePath in Directory.EnumerateFiles(envFolder, "*.bru", SearchOption.TopDirectoryOnly)
                     .Where(p => !IsGlobalEnvironmentFile(p)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fileName = Path.GetFileName(filePath);
                var color = meta.EnvironmentColors.TryGetValue(fileName, out var c) ? c : null;
                var id = meta.EnvironmentIds.TryGetValue(fileName, out var g) ? g : Guid.NewGuid();
                var env = await LoadEnvironmentCoreAsync(filePath, id, ct).ConfigureAwait(false);
                results.Add(env with { Color = color });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable Bruno environment file: {File}", filePath);
            }
        }

        results.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return ApplyMetaOrder(results, meta.EnvironmentOrder);
    }

    private static IReadOnlyList<EnvironmentModel> ApplyMetaOrder(
        List<EnvironmentModel> alphabetical, IReadOnlyList<string> order)
    {
        if (order.Count == 0) return alphabetical;

        var byFileName = alphabetical.ToDictionary(
            e => Path.GetFileName(e.FilePath), StringComparer.OrdinalIgnoreCase);

        var ordered = order
            .Where(byFileName.ContainsKey)
            .Select(n => byFileName[n])
            .ToList();

        var inOrder = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
        var remaining = alphabetical.Where(
            e => !inOrder.Contains(Path.GetFileName(e.FilePath)));

        return [..ordered, ..remaining];
    }

    public async Task<EnvironmentModel> LoadEnvironmentAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var collectionPath = GetCollectionFolderPath(filePath);
        var meta = await _meta.LoadAsync(collectionPath, ct).ConfigureAwait(false);
        var fileName = Path.GetFileName(filePath);
        var id = meta.EnvironmentIds.TryGetValue(fileName, out var g) ? g : Guid.NewGuid();
        var model = await LoadEnvironmentCoreAsync(filePath, id, ct).ConfigureAwait(false);
        var color = meta.EnvironmentColors.TryGetValue(fileName, out var c) ? c : null;
        return model with { Color = color };
    }

    /// <summary>
    /// Loads an environment from its <c>.bru</c> file and injects secrets, but does not
    /// consult the app-data meta for color or id. Used internally when meta is already loaded.
    /// </summary>
    private async Task<EnvironmentModel> LoadEnvironmentCoreAsync(string filePath, Guid environmentId, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Environment file not found: '{filePath}'", filePath);

        var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var doc = BruParser.Parse(text);

        var name = Path.GetFileNameWithoutExtension(filePath);
        var variables = new List<EnvironmentVariable>();

        // vars {} — non-secret variables (skip disabled ~ entries)
        if (doc.Find("vars") is { } varsBlock)
        {
            foreach (var kv in varsBlock.Items.Where(kv => kv.IsEnabled))
                variables.Add(new EnvironmentVariable { Name = kv.Key, Value = kv.Value });
        }

        // vars:secret {} — secret variables (skip disabled ~ entries); actual values come
        // from local secret storage, not from the .bru file.
        if (doc.Find("vars:secret") is { } secretBlock)
        {
            foreach (var kv in secretBlock.Items.Where(kv => kv.IsEnabled))
                variables.Add(new EnvironmentVariable { Name = kv.Key, Value = kv.Value, IsSecret = true });
        }

        var model = new EnvironmentModel { FilePath = filePath, Name = name, Variables = variables, EnvironmentId = environmentId };
        return model with { Variables = await InjectSecretsAsync(model, ct).ConfigureAwait(false) };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Save / Create / Delete / Rename / Clone
    // ─────────────────────────────────────────────────────────────────────────

    public async Task SaveEnvironmentAsync(EnvironmentModel environment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // Global environment is stored in app-data, not as a .bru file.
        if (IsGlobalEnvironmentFile(environment.FilePath))
        {
            await SaveGlobalEnvironmentAsync(environment, ct).ConfigureAwait(false);
            return;
        }

        var directory = Path.GetDirectoryName(environment.FilePath)!;
        Directory.CreateDirectory(directory);

        // Persist actual secret values to local storage before writing the file.
        await PersistSecretsAsync(environment, ct).ConfigureAwait(false);

        // Re-read the existing file (if any) to preserve disabled vars.
        BruDocument? existing = null;
        if (File.Exists(environment.FilePath))
        {
            try
            {
                var existingText = await File.ReadAllTextAsync(environment.FilePath, ct).ConfigureAwait(false);
                existing = BruParser.Parse(existingText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not re-read '{Path}' for round-trip; starting fresh", environment.FilePath);
            }
        }

        var content = BuildEnvContent(environment, existing);
        await File.WriteAllTextAsync(environment.FilePath, content, ct).ConfigureAwait(false);

        // Persist env color and id to app-data meta (Bruno .bru files have no color or id field).
        var collectionPath = GetCollectionFolderPath(environment.FilePath);
        var meta = await _meta.LoadAsync(collectionPath, ct).ConfigureAwait(false);
        var newColors = new Dictionary<string, string>(meta.EnvironmentColors);
        var newIds = new Dictionary<string, Guid>(meta.EnvironmentIds);
        var envFileName = Path.GetFileName(environment.FilePath);
        if (environment.Color is not null)
            newColors[envFileName] = environment.Color;
        else
            newColors.Remove(envFileName);
        newIds[envFileName] = environment.EnvironmentId;
        await _meta.SaveAsync(collectionPath, new BrunoCollectionMeta
        {
            EnvironmentOrder = meta.EnvironmentOrder,
            EnvironmentColors = newColors,
            EnvironmentIds = newIds,
            GlobalVariables = meta.GlobalVariables,
            GlobalSecretVariables = meta.GlobalSecretVariables,
            GlobalEnvironmentId = meta.GlobalEnvironmentId,
            GlobalPreviewEnvironmentName = meta.GlobalPreviewEnvironmentName,
        }, ct).ConfigureAwait(false);

        _logger.LogDebug("Saved Bruno environment '{Name}' → {Path}", environment.Name, environment.FilePath);
    }

    public async Task<EnvironmentModel> CreateEnvironmentAsync(
        string collectionFolderPath, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var envFolder = GetEnvFolder(collectionFolderPath);
        var filePath = Path.Combine(envFolder, SanitizeFileName(name) + EnvironmentFileExtension);

        if (File.Exists(filePath))
            throw new InvalidOperationException(
                $"An environment named '{name}' already exists in '{collectionFolderPath}'.");

        var model = new EnvironmentModel { FilePath = filePath, Name = name, EnvironmentId = Guid.NewGuid() };
        await SaveEnvironmentAsync(model, ct).ConfigureAwait(false);
        return model;
    }

    public async Task DeleteEnvironmentAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ct.ThrowIfCancellationRequested();

        // Remove locally-stored secrets for this environment before deleting the file.
        var collectionPath = GetCollectionFolderPath(filePath);
        var envName = Path.GetFileNameWithoutExtension(filePath);
        await _secrets.DeleteEnvironmentSecretsAsync(collectionPath, envName, ct).ConfigureAwait(false);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogDebug("Deleted Bruno environment file: {Path}", filePath);
        }
    }

    public async Task<EnvironmentModel> RenameEnvironmentAsync(
        string filePath, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var directory = Path.GetDirectoryName(filePath)!;
        var newFilePath = Path.Combine(directory, SanitizeFileName(newName) + EnvironmentFileExtension);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Environment file not found: '{filePath}'", filePath);

        if (File.Exists(newFilePath) && !string.Equals(filePath, newFilePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"An environment named '{newName}' already exists.");

        var existing = await LoadEnvironmentAsync(filePath, ct).ConfigureAwait(false);
        var renamed = existing with { FilePath = newFilePath, Name = newName };
        await SaveEnvironmentAsync(renamed, ct).ConfigureAwait(false);

        if (!string.Equals(filePath, newFilePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(filePath);

            // Clean up secrets stored under the old file-name key.
            var oldEnvName = Path.GetFileNameWithoutExtension(filePath);
            var newEnvName = Path.GetFileNameWithoutExtension(newFilePath);
            if (!string.Equals(oldEnvName, newEnvName, StringComparison.OrdinalIgnoreCase))
            {
                var collectionPath = GetCollectionFolderPath(filePath);
                await _secrets.DeleteEnvironmentSecretsAsync(collectionPath, oldEnvName, ct)
                    .ConfigureAwait(false);
            }
        }

        _logger.LogDebug("Renamed Bruno environment '{Old}' → '{New}'", filePath, newFilePath);
        return renamed;
    }

    public async Task<EnvironmentModel> CloneEnvironmentAsync(
        string sourceFilePath, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var directory = Path.GetDirectoryName(sourceFilePath)!;
        var newFilePath = Path.Combine(directory, SanitizeFileName(newName) + EnvironmentFileExtension);

        if (File.Exists(newFilePath))
            throw new InvalidOperationException($"An environment named '{newName}' already exists.");

        var source = await LoadEnvironmentAsync(sourceFilePath, ct).ConfigureAwait(false);

        // Clones do not inherit secrets — the developer must supply their own values.
        var cloned = source with
        {
            FilePath = newFilePath,
            Name = newName,
            EnvironmentId = Guid.NewGuid(),
            Variables = source.Variables
                .Select(v => v.IsSecret
                    ? new EnvironmentVariable { Name = v.Name, Value = string.Empty, VariableType = v.VariableType, IsSecret = true }
                    : v)
                .ToList(),
        };
        await SaveEnvironmentAsync(cloned, ct).ConfigureAwait(false);

        _logger.LogDebug("Cloned Bruno environment '{Source}' → '{New}'", sourceFilePath, newFilePath);
        return cloned;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Environment order
    // ─────────────────────────────────────────────────────────────────────────

    public async Task SaveEnvironmentOrderAsync(
        string collectionFolderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(orderedNames);

        // Order is stored in app-data meta, not as _meta.json inside the Bruno repo.
        var meta = await _meta.LoadAsync(collectionFolderPath, ct).ConfigureAwait(false);
        await _meta.SaveAsync(collectionFolderPath, new BrunoCollectionMeta
        {
            EnvironmentOrder = orderedNames.ToList(),
            EnvironmentColors = meta.EnvironmentColors,
            EnvironmentIds = meta.EnvironmentIds,
            GlobalVariables = meta.GlobalVariables,
            GlobalSecretVariables = meta.GlobalSecretVariables,
            GlobalEnvironmentId = meta.GlobalEnvironmentId,
            GlobalPreviewEnvironmentName = meta.GlobalPreviewEnvironmentName,
        }, ct).ConfigureAwait(false);
        _logger.LogDebug("Saved Bruno environment order for '{Path}'", collectionFolderPath);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildEnvContent(EnvironmentModel environment, BruDocument? existing)
    {
        var regularVars = environment.Variables.Where(v => !v.IsSecret).ToList();
        var secretVars = environment.Variables.Where(v => v.IsSecret).ToList();

        // Block-targeted update: start from existing blocks to preserve the original
        // ordering and separator whitespace (prevents adding blank lines on no-op saves).
        var blocks = existing is not null
            ? new List<BruBlock>(existing.Blocks)
            : new List<BruBlock>();

        // vars block (non-secret) — update in-place or insert when not present
        var disabledVars = GetDisabledItems(existing, "vars");
        if (regularVars.Count > 0 || disabledVars.Count > 0)
        {
            var varsBlock = new BruBlock("vars");
            foreach (var v in regularVars)
                varsBlock.Items.Add(new BruKv(v.Name, v.Value));
            foreach (var kv in disabledVars)
                varsBlock.Items.Add(kv);
            SetOrInsertAt(blocks, "vars", varsBlock, 0);
        }
        else
        {
            // Remove the vars block if there are no regular vars (not even disabled).
            RemoveEnvBlock(blocks, "vars");
        }

        // vars:secret block — Bruno stores only secret variable names in the file; actual
        // values live in local app-data. Preserve disabled secret names without values too.
        var disabledSecretVars = GetDisabledItems(existing, "vars:secret");
        if (secretVars.Count > 0 || disabledSecretVars.Count > 0)
        {
            var secretBlock = new BruBlock("vars:secret");
            foreach (var v in secretVars)
                secretBlock.Items.Add(new BruKv(v.Name, string.Empty));
            foreach (var kv in disabledSecretVars)
                secretBlock.Items.Add(new BruKv(kv.Key, string.Empty, kv.IsEnabled));
            SetOrInsertAfter(blocks, "vars:secret", secretBlock, "vars");
        }
        else
        {
            RemoveEnvBlock(blocks, "vars:secret");
        }

        // If there are no variables at all, emit an empty vars block so the file is valid.
        if (blocks.Count == 0)
            blocks.Add(new BruBlock("vars"));

        return BruWriter.Write(blocks, existing?.LineEnding ?? "\n");
    }

    private static void SetOrInsertAt(List<BruBlock> blocks, string name, BruBlock block, int fallbackIndex)
    {
        var idx = blocks.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
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

    private static void SetOrInsertAfter(List<BruBlock> blocks, string name, BruBlock block, string afterName)
    {
        var idx = blocks.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            block.HasPrecedingBlankLine = blocks[idx].HasPrecedingBlankLine;
            blocks[idx] = block;
            return;
        }
        var afterIdx = blocks.FindIndex(b => string.Equals(b.Name, afterName, StringComparison.OrdinalIgnoreCase));
        block.HasPrecedingBlankLine = true;
        blocks.Insert(afterIdx >= 0 ? afterIdx + 1 : blocks.Count, block);
    }

    private static void RemoveEnvBlock(List<BruBlock> blocks, string name)
    {
        var idx = blocks.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) blocks.RemoveAt(idx);
    }

    private static IReadOnlyList<BruKv> GetDisabledItems(BruDocument? doc, string blockName) =>
        doc?.Find(blockName)?.Items.Where(kv => !kv.IsEnabled).ToList()
        ?? (IReadOnlyList<BruKv>)[];

    private static string GetEnvFolder(string collectionFolderPath) =>
        Path.Combine(collectionFolderPath, EnvironmentFolderName);

    /// <summary>
    /// Given the path of an environment file, returns the collection root (two levels up:
    /// <c>collection/environments/name.bru</c> → <c>collection</c>).
    /// </summary>
    private static string GetCollectionFolderPath(string filePath)
    {
        var envFolder = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        return Path.GetDirectoryName(envFolder)!;
    }

    /// <summary>Looks up each secret variable's actual value from local storage.</summary>
    private async Task<IReadOnlyList<EnvironmentVariable>> InjectSecretsAsync(
        EnvironmentModel model, CancellationToken ct)
    {
        if (!model.Variables.Any(v => v.IsSecret))
            return model.Variables;

        var collectionPath = GetCollectionFolderPath(model.FilePath);
        var envName = Path.GetFileNameWithoutExtension(model.FilePath);

        var result = new List<EnvironmentVariable>(model.Variables.Count);
        foreach (var v in model.Variables)
        {
            // Do not modify non-secret, or non-static variables.
            if (!v.IsSecret || v.VariableType != VariableTypes.Static)
            {
                result.Add(v);
                continue;
            }

            var stored = await _secrets
                .GetSecretAsync(collectionPath, envName, v.Name, ct)
                .ConfigureAwait(false);

            result.Add(new EnvironmentVariable
            {
                Name = v.Name,
                Value = stored ?? string.Empty,
                VariableType = v.VariableType,
                MockDataCategory = v.MockDataCategory,
                MockDataField = v.MockDataField,
                ResponseRequestName = v.ResponseRequestName,
                ResponsePath = v.ResponsePath,
                ResponseMatcher = v.ResponseMatcher,
                ResponseFrequency = v.ResponseFrequency,
                ResponseExpiresAfterSeconds = v.ResponseExpiresAfterSeconds,
                IsSecret = true,
            });
        }
        return result;
    }

    /// <summary>Writes each secret variable's actual value to local storage.</summary>
    private async Task PersistSecretsAsync(EnvironmentModel environment, CancellationToken ct)
    {
        var secretVars = environment.Variables.Where(v => v.IsSecret).ToList();
        if (secretVars.Count == 0) return;

        var collectionPath = GetCollectionFolderPath(environment.FilePath);
        var envName = Path.GetFileNameWithoutExtension(environment.FilePath);

        foreach (var v in secretVars)
        {
            await _secrets
                .SetSecretAsync(collectionPath, envName, v.Name, v.Value, ct)
                .ConfigureAwait(false);
        }
    }

    // ─── Global environment ──────────────────────────────────────────────────

    public async Task<EnvironmentModel> LoadGlobalEnvironmentAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        // Virtual path — used as a stable key for routing and secret storage.
        // The actual file is never written to the Bruno repo.
        var filePath = Path.Combine(GetEnvFolder(collectionFolderPath),
            GlobalEnvironmentFileName + EnvironmentFileExtension);

        var meta = await _meta.LoadAsync(collectionFolderPath, ct).ConfigureAwait(false);
        var globalId = meta.GlobalEnvironmentId ?? Guid.NewGuid();

        if (meta.GlobalVariables.Count == 0 && meta.GlobalSecretVariables.Count == 0)
            return new EnvironmentModel
            {
                FilePath = filePath,
                Name = "Global",
                Variables = [],
                EnvironmentId = globalId,
                GlobalPreviewEnvironmentName = meta.GlobalPreviewEnvironmentName,
            };

        var variables = meta.GlobalVariables
            .Select(v => new EnvironmentVariable
            {
                Name = v.Name,
                Value = v.Value,
                VariableType = v.VariableType ?? EnvironmentVariable.VariableTypes.Static,
                MockDataCategory = v.MockDataCategory,
                MockDataField = v.MockDataField,
                ResponseRequestName = v.ResponseRequestName,
                ResponsePath = v.ResponsePath,
                ResponseMatcher = v.ResponseMatcher ?? ResponseValueMatcher.JsonPath,
                ResponseFrequency = v.ResponseFrequency ?? DynamicFrequency.Always,
                ResponseExpiresAfterSeconds = v.ResponseExpiresAfterSeconds,
                IsForceGlobalOverride = v.IsForceGlobalOverride ?? false,
            })
            .Concat(meta.GlobalSecretVariables
                .Select(s => new EnvironmentVariable
                {
                    Name = s.Name,
                    Value = string.Empty,
                    VariableType = s.VariableType ?? EnvironmentVariable.VariableTypes.Static,
                    MockDataCategory = s.MockDataCategory,
                    MockDataField = s.MockDataField,
                    ResponseRequestName = s.ResponseRequestName,
                    ResponsePath = s.ResponsePath,
                    ResponseMatcher = s.ResponseMatcher ?? ResponseValueMatcher.JsonPath,
                    ResponseFrequency = s.ResponseFrequency ?? DynamicFrequency.Always,
                    ResponseExpiresAfterSeconds = s.ResponseExpiresAfterSeconds,
                    IsSecret = true,
                    IsForceGlobalOverride = s.IsForceGlobalOverride ?? false,
                }))
            .ToList();

        var model = new EnvironmentModel
        {
            FilePath = filePath,
            Name = "Global",
            Variables = variables,
            EnvironmentId = globalId,
            GlobalPreviewEnvironmentName = meta.GlobalPreviewEnvironmentName,
        };
        return model with { Variables = await InjectSecretsAsync(model, ct).ConfigureAwait(false) };
    }

    public async Task SaveGlobalEnvironmentAsync(
        EnvironmentModel globalEnvironment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(globalEnvironment);

        // Persist actual secret values to local storage.
        await PersistSecretsAsync(globalEnvironment, ct).ConfigureAwait(false);

        var collectionPath = GetCollectionFolderPath(globalEnvironment.FilePath);
        var meta = await _meta.LoadAsync(collectionPath, ct).ConfigureAwait(false);

        var nonSecretVars = globalEnvironment.Variables
            .Where(v => !v.IsSecret)
            .Select(v => new BrunoCollectionMeta.GlobalVarEntry
            {
                Name = v.Name,
                Value = v.Value,
                VariableType = v.VariableType == EnvironmentVariable.VariableTypes.Static ? null : v.VariableType,
                MockDataCategory = v.MockDataCategory,
                MockDataField = v.MockDataField,
                ResponseRequestName = v.ResponseRequestName,
                ResponsePath = v.ResponsePath,
                ResponseMatcher = v.ResponseMatcher == ResponseValueMatcher.JsonPath ? null : v.ResponseMatcher,
                ResponseFrequency = v.ResponseFrequency == DynamicFrequency.Always ? null : v.ResponseFrequency,
                ResponseExpiresAfterSeconds = v.ResponseExpiresAfterSeconds,
                IsForceGlobalOverride = v.IsForceGlobalOverride ? (bool?)true : null,
            })
            .ToList();

        var secretVars = globalEnvironment.Variables
            .Where(v => v.IsSecret)
            .Select(v => new BrunoCollectionMeta.GlobalSecretVarEntry
            {
                Name = v.Name,
                VariableType = v.VariableType == EnvironmentVariable.VariableTypes.Static ? null : v.VariableType,
                MockDataCategory = v.MockDataCategory,
                MockDataField = v.MockDataField,
                ResponseRequestName = v.ResponseRequestName,
                ResponsePath = v.ResponsePath,
                ResponseMatcher = v.ResponseMatcher == ResponseValueMatcher.JsonPath ? null : v.ResponseMatcher,
                ResponseFrequency = v.ResponseFrequency == DynamicFrequency.Always ? null : v.ResponseFrequency,
                ResponseExpiresAfterSeconds = v.ResponseExpiresAfterSeconds,
                IsForceGlobalOverride = v.IsForceGlobalOverride ? (bool?)true : null,
            })
            .ToList();

        await _meta.SaveAsync(collectionPath, new BrunoCollectionMeta
        {
            EnvironmentOrder = meta.EnvironmentOrder,
            EnvironmentColors = meta.EnvironmentColors,
            EnvironmentIds = meta.EnvironmentIds,
            GlobalVariables = nonSecretVars,
            GlobalSecretVariables = secretVars,
            GlobalEnvironmentId = globalEnvironment.EnvironmentId,
            GlobalPreviewEnvironmentName = globalEnvironment.GlobalPreviewEnvironmentName,
        }, ct).ConfigureAwait(false);
        _logger.LogDebug("Saved Bruno global environment for '{Path}'", collectionPath);
    }

    private static bool IsGlobalEnvironmentFile(string filePath) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(filePath),
            GlobalEnvironmentFileName,
            StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join('_', name.Split(invalid, StringSplitOptions.None));
    }
}
