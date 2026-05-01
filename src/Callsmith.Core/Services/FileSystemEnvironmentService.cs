using System.Text.Json;
using System.Text.Json.Serialization;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;
using static Callsmith.Core.Models.EnvironmentVariable;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="IEnvironmentService"/> implementation that stores environments as
/// <c>.env.callsmith</c> JSON files in the <c>environment/</c> sub-folder of a collection.
/// </summary>
public sealed class FileSystemEnvironmentService : IEnvironmentService
{
    /// <summary>File extension used for all environment files.</summary>
    public const string EnvironmentFileExtension = ".env.callsmith";

    /// <summary>File name (without extension) of the collection-scoped global environment.</summary>
    public const string GlobalEnvironmentFileName = "_global";

    /// <summary>
    /// Name of the JSON file that records the user's preferred environment display order,
    /// stored alongside the <c>.env.callsmith</c> files in the <c>environment/</c> folder.
    /// </summary>
    public const string MetaFileName = "_meta.json";

    private readonly ISecretStorageService _secrets;
    private readonly ILogger<FileSystemEnvironmentService> _logger;

    /// <summary>Initialises the service with the provided dependencies.</summary>
    public FileSystemEnvironmentService(
        ISecretStorageService secrets,
        ILogger<FileSystemEnvironmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = secrets;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EnvironmentModel>> ListEnvironmentsAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        var envFolder = GetEnvFolder(collectionFolderPath);
        if (!Directory.Exists(envFolder))
            return [];

        var results = new List<EnvironmentModel>();
        foreach (var filePath in Directory.EnumerateFiles(
                     envFolder, $"*{EnvironmentFileExtension}", SearchOption.TopDirectoryOnly)
                     .Where(p => !IsGlobalEnvironmentFile(p)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var env = await LoadEnvironmentAsync(filePath, ct).ConfigureAwait(false);
                results.Add(env);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable environment file: {File}", filePath);
            }
        }

        results.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return ApplyOrder(results, envFolder);
    }

    private static IReadOnlyList<EnvironmentModel> ApplyOrder(
        List<EnvironmentModel> alphabetical, string envFolder)
    {
        var metaFilePath = Path.Combine(envFolder, MetaFileName);
        if (!File.Exists(metaFilePath)) return alphabetical;

        IReadOnlyList<string> savedOrder;
        try
        {
            var json = File.ReadAllText(metaFilePath);
            var dto = JsonSerializer.Deserialize<EnvironmentMetaDto>(json) ?? new EnvironmentMetaDto();
            savedOrder = dto.Order ?? [];
        }
        catch (JsonException) { return alphabetical; }

        if (savedOrder.Count == 0) return alphabetical;

        var byFileName = alphabetical.ToDictionary(
            e => Path.GetFileName(e.FilePath), StringComparer.OrdinalIgnoreCase);

        var ordered = savedOrder
            .Where(byFileName.ContainsKey)
            .Select(n => byFileName[n])
            .ToList();

        var inOrder = new HashSet<string>(savedOrder, StringComparer.OrdinalIgnoreCase);
        var remaining = alphabetical.Where(
            e => !inOrder.Contains(Path.GetFileName(e.FilePath)));

        return [..ordered, ..remaining];
    }

    /// <inheritdoc/>
    public async Task<EnvironmentModel> LoadEnvironmentAsync(
        string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Environment file not found: '{filePath}'", filePath);

        await using var stream = File.OpenRead(filePath);
        var dto = await JsonSerializer.DeserializeAsync<EnvironmentDto>(stream, CallsmithJsonOptions.Default, ct)
                      .ConfigureAwait(false)
                  ?? throw new InvalidDataException($"Environment file is empty or null: '{filePath}'");

        var model = DtoToModel(filePath, dto);
        return model with { Variables = await InjectSecretsAsync(model, ct).ConfigureAwait(false) };
    }

    /// <inheritdoc/>
    public async Task SaveEnvironmentsAsync(
        IReadOnlyList<EnvironmentModel> environments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environments);
        if (environments.Count == 0) return;

        // Collect all secrets for all environments and write them in a single
        // bulk operation — one read-modify-write on the secrets file regardless of
        // how many environments are being saved.
        var allSecrets = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            environments.Count, StringComparer.Ordinal);

        foreach (var env in environments)
        {
            var secretVars = env.Variables.Where(v => v.IsSecret).ToList();
            if (secretVars.Count == 0) continue;

            var envName = Path.GetFileNameWithoutExtension(env.FilePath);
            var bulk = new Dictionary<string, string>(secretVars.Count, StringComparer.Ordinal);
            foreach (var v in secretVars)
                bulk[v.Name] = v.Value;
            allSecrets[envName] = bulk;
        }

        if (allSecrets.Count > 0)
        {
            var collectionPath = GetCollectionFolderPath(environments[0].FilePath);
            await _secrets
                .SetCollectionSecretsAsync(collectionPath, allSecrets, ct)
                .ConfigureAwait(false);
        }

        // Write each environment's JSON file. Secrets are already persisted above so
        // we write the DTO directly rather than going through SaveEnvironmentAsync
        // (which would call PersistSecretsAsync a second time).
        // Use an atomic write (temp file → rename) so that transient file locks from
        // AV scanners or cloud-sync clients do not cause IOException: the write targets
        // a unique temporary file and the final rename is instantaneous on the same
        // volume, preventing any window where the destination is locked mid-write.
        foreach (var environment in environments)
        {
            var directory = Path.GetDirectoryName(environment.FilePath)!;
            Directory.CreateDirectory(directory);

            var dto = ModelToDto(environment);
            // Use a GUID-suffixed temp file so concurrent processes saving the same
            // collection cannot collide on the temp path.
            var tempPath = environment.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write))
                    await JsonSerializer.SerializeAsync(stream, dto, CallsmithJsonOptions.Default, ct).ConfigureAwait(false);

                File.Move(tempPath, environment.FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }

            _logger.LogDebug("Saved environment '{Name}' → {Path}", environment.Name, environment.FilePath);
        }
    }

    /// <inheritdoc/>
    public async Task SaveEnvironmentAsync(EnvironmentModel environment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var directory = Path.GetDirectoryName(environment.FilePath)!;
        Directory.CreateDirectory(directory);

        // Persist actual secret values to local storage before writing the file.
        await PersistSecretsAsync(environment, ct).ConfigureAwait(false);

        // The serialised file stores an empty value for secrets — only the name (presence)
        // is recorded so that the file is safe to check in to version control.
        var dto = ModelToDto(environment);

        await using var stream = File.Open(environment.FilePath, FileMode.Create, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, dto, CallsmithJsonOptions.Default, ct).ConfigureAwait(false);

        _logger.LogDebug("Saved environment '{Name}' → {Path}", environment.Name, environment.FilePath);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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
            _logger.LogDebug("Deleted environment file: {Path}", filePath);
        }
    }

    /// <inheritdoc/>
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
            throw new InvalidOperationException(
                $"An environment named '{newName}' already exists.");

        var existing = await LoadEnvironmentAsync(filePath, ct).ConfigureAwait(false);
        var renamed = existing with { FilePath = newFilePath, Name = newName };

        // SaveEnvironmentAsync persists secrets under the new file-name key.
        await SaveEnvironmentAsync(renamed, ct).ConfigureAwait(false);
        File.Delete(filePath);

        // Clean up secrets stored under the old file-name key (only when the key actually changes).
        var oldEnvName = Path.GetFileNameWithoutExtension(filePath);
        var newEnvName = Path.GetFileNameWithoutExtension(newFilePath);
        if (!string.Equals(oldEnvName, newEnvName, StringComparison.OrdinalIgnoreCase))
        {
            var collectionPath = GetCollectionFolderPath(filePath);
            await _secrets.DeleteEnvironmentSecretsAsync(collectionPath, oldEnvName, ct)
                .ConfigureAwait(false);
        }

        _logger.LogDebug("Renamed environment '{Old}' → '{New}'", filePath, newFilePath);
        return renamed;
    }

    /// <inheritdoc/>
    public async Task<EnvironmentModel> CloneEnvironmentAsync(
        string sourceFilePath, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var directory = Path.GetDirectoryName(sourceFilePath)!;
        var newFilePath = Path.Combine(directory, SanitizeFileName(newName) + EnvironmentFileExtension);

        if (File.Exists(newFilePath))
            throw new InvalidOperationException(
                $"An environment named '{newName}' already exists.");

        var source = await LoadEnvironmentAsync(sourceFilePath, ct).ConfigureAwait(false);

        // Clones do not inherit secrets — the developer must supply their own values.
        var cloned = source with
        {
            FilePath = newFilePath,
            Name = newName,
            EnvironmentId = Guid.NewGuid(),
            Variables = source.Variables
                .Select(v => v.IsSecret
                    ? new EnvironmentVariable { Name = v.Name, Value = string.Empty, VariableType = v.VariableType, IsSecret = true, Segments = v.Segments }
                    : v)
                .ToList(),
        };

        await SaveEnvironmentAsync(cloned, ct).ConfigureAwait(false);

        _logger.LogDebug("Cloned environment '{Source}' → '{New}'", sourceFilePath, newFilePath);
        return cloned;
    }

    // ─── Environment order ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task SaveEnvironmentOrderAsync(
        string collectionFolderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentNullException.ThrowIfNull(orderedNames);

        var envFolder = GetEnvFolder(collectionFolderPath);
        var metaFilePath = Path.Combine(envFolder, MetaFileName);

        if (orderedNames.Count == 0)
        {
            if (File.Exists(metaFilePath))
            {
                File.Delete(metaFilePath);
                _logger.LogDebug("Removed environment meta file for '{Path}'", collectionFolderPath);
            }
            return;
        }

        Directory.CreateDirectory(envFolder);
        var dto = new EnvironmentMetaDto { Order = [..orderedNames] };
        var json = JsonSerializer.Serialize(dto, CallsmithJsonOptions.Default);
        await File.WriteAllTextAsync(metaFilePath, json, ct).ConfigureAwait(false);
        _logger.LogDebug("Saved environment order for '{Path}'", collectionFolderPath);
    }

    // ─── Global environment ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<EnvironmentModel> LoadGlobalEnvironmentAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        var filePath = GetGlobalFilePath(collectionFolderPath);
        if (!File.Exists(filePath))
            return new EnvironmentModel { FilePath = filePath, Name = "Global", Variables = [], EnvironmentId = Guid.NewGuid() };

        return await LoadEnvironmentAsync(filePath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task SaveGlobalEnvironmentAsync(
        EnvironmentModel globalEnvironment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(globalEnvironment);
        return SaveEnvironmentAsync(globalEnvironment, ct);
    }

    // ─── Private helpers ────────────────────────────────────────────────────

    private static string GetGlobalFilePath(string collectionFolderPath) =>
        Path.Combine(GetEnvFolder(collectionFolderPath),
            GlobalEnvironmentFileName + EnvironmentFileExtension);

    private static bool IsGlobalEnvironmentFile(string filePath) =>
        string.Equals(
            Path.GetFileName(filePath),
            GlobalEnvironmentFileName + EnvironmentFileExtension,
            StringComparison.OrdinalIgnoreCase);

    private static string GetEnvFolder(string collectionFolderPath) =>
        Path.Combine(collectionFolderPath, FileSystemCollectionService.EnvironmentFolderName);

    /// <summary>
    /// Given the path of an environment file, returns the collection root (two levels up:
    /// <c>collection/environment/name.env.callsmith</c> → <c>collection</c>).
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
                IsSecret = true,
                Segments = v.Segments,
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

        // Collect all secrets into a dictionary with explicit overwrite semantics so
        // that duplicate variable names (last one wins) do not throw, matching the
        // original per-variable loop behaviour.
        var bulk = new Dictionary<string, string>(secretVars.Count, StringComparer.Ordinal);
        foreach (var v in secretVars)
            bulk[v.Name] = v.Value;
        await _secrets
            .SetEnvironmentSecretsAsync(collectionPath, envName, bulk, ct)
            .ConfigureAwait(false);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join('_', name.Split(invalid, StringSplitOptions.None));
    }

    private static EnvironmentModel DtoToModel(string filePath, EnvironmentDto dto) => new()
    {
        FilePath = filePath,
        EnvironmentId = dto.EnvironmentId,
        Name = dto.Name ?? Path.GetFileNameWithoutExtension(filePath),
        Color = dto.Color,
        GlobalPreviewEnvironmentName = dto.GlobalPreviewEnvironmentName,
        Variables = (dto.Variables ?? [])
            .Select(v => new EnvironmentVariable
            {
                Name = v.Name ?? string.Empty,
                Value = v.Value ?? string.Empty,
                VariableType = v.VariableType ?? EnvironmentVariable.VariableTypes.Static,
                IsSecret = v.IsSecret ?? false,
                Segments = v.Segments is { Count: > 0 } ? v.Segments : null,
                MockDataCategory = v.MockDataCategory,
                MockDataField = v.MockDataField,
                ResponseRequestName = v.ResponseRequestName,
                ResponsePath = v.ResponsePath,
                ResponseMatcher = v.ResponseMatcher ?? ResponseValueMatcher.JsonPath,
                ResponseFrequency = v.ResponseFrequency ?? DynamicFrequency.Always,
                ResponseExpiresAfterSeconds = v.ResponseExpiresAfterSeconds,
                IsForceGlobalOverride = v.IsForceGlobalOverride ?? false,
            })
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .ToList(),
    };

    private static EnvironmentDto ModelToDto(EnvironmentModel model) => new()
    {
        EnvironmentId = model.EnvironmentId,
        Name = model.Name,
        Color = model.Color,
        GlobalPreviewEnvironmentName = model.GlobalPreviewEnvironmentName,
        Variables = model.Variables
            .Select(v => new VariableDto
            {
                Name = v.Name,
                // Secret values are stored in local app-data, not in the file.
                Value = v.IsSecret ? string.Empty : v.Value,
                VariableType = v.VariableType == EnvironmentVariable.VariableTypes.Static
                    ? null  // omit the default to keep JSON tidy
                    : v.VariableType,
                IsSecret = v.IsSecret ? (bool?)true : null,
                Segments = v.Segments is { Count: > 0 } ? [.. v.Segments] : null,
                MockDataCategory = v.MockDataCategory,
                MockDataField = v.MockDataField,
                ResponseRequestName = v.ResponseRequestName,
                ResponsePath = v.ResponsePath,
                ResponseMatcher = v.ResponseMatcher == ResponseValueMatcher.JsonPath ? null : v.ResponseMatcher,
                ResponseFrequency = v.ResponseFrequency == DynamicFrequency.Always ? null : v.ResponseFrequency,
                ResponseExpiresAfterSeconds = v.ResponseExpiresAfterSeconds,
                IsForceGlobalOverride = v.IsForceGlobalOverride ? (bool?)true : null,
            })
            .ToList(),
    };

    // ─── Private DTOs (JSON shape) ──────────────────────────────────────────

    private sealed class EnvironmentDto
    {
        public Guid EnvironmentId { get; init; }
        public string? Name { get; init; }
        public string? Color { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GlobalPreviewEnvironmentName { get; init; }
        public List<VariableDto>? Variables { get; init; }
    }

    private sealed class VariableDto
    {
        public string? Name { get; init; }
        public string? Value { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? VariableType { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsSecret { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueSegment>? Segments { get; init; }
        // Mock-data type
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MockDataCategory { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MockDataField { get; init; }
        // Response-body type
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResponseRequestName { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResponsePath { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponseValueMatcher? ResponseMatcher { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DynamicFrequency? ResponseFrequency { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ResponseExpiresAfterSeconds { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsForceGlobalOverride { get; init; }
    }

    /// <summary>
    /// The JSON structure written to and read from the <c>_meta.json</c> environment metadata file.
    /// </summary>
    private sealed class EnvironmentMetaDto
    {
        [JsonPropertyName("order")]
        public List<string>? Order { get; set; }
    }
}
