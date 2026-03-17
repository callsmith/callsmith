using System.Text.Json;
using System.Text.Json.Serialization;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<FileSystemEnvironmentService> _logger;

    /// <summary>Initialises the service with the provided logger.</summary>
    public FileSystemEnvironmentService(ILogger<FileSystemEnvironmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
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
        return results;
    }

    /// <inheritdoc/>
    public async Task<EnvironmentModel> LoadEnvironmentAsync(
        string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Environment file not found: '{filePath}'", filePath);

        await using var stream = File.OpenRead(filePath);
        var dto = await JsonSerializer.DeserializeAsync<EnvironmentDto>(stream, JsonOptions, ct)
                      .ConfigureAwait(false)
                  ?? throw new InvalidDataException($"Environment file is empty or null: '{filePath}'");

        return DtoToModel(filePath, dto);
    }

    /// <inheritdoc/>
    public async Task SaveEnvironmentAsync(EnvironmentModel environment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var directory = Path.GetDirectoryName(environment.FilePath)!;
        Directory.CreateDirectory(directory);

        var dto = ModelToDto(environment);

        await using var stream = File.Open(environment.FilePath, FileMode.Create, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, ct).ConfigureAwait(false);

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

        var model = new EnvironmentModel { FilePath = filePath, Name = name };
        await SaveEnvironmentAsync(model, ct).ConfigureAwait(false);
        return model;
    }

    /// <inheritdoc/>
    public Task DeleteEnvironmentAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ct.ThrowIfCancellationRequested();

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogDebug("Deleted environment file: {Path}", filePath);
        }

        return Task.CompletedTask;
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

        await SaveEnvironmentAsync(renamed, ct).ConfigureAwait(false);
        File.Delete(filePath);

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
        var cloned = source with { FilePath = newFilePath, Name = newName };

        await SaveEnvironmentAsync(cloned, ct).ConfigureAwait(false);

        _logger.LogDebug("Cloned environment '{Source}' → '{New}'", sourceFilePath, newFilePath);
        return cloned;
    }

    // ─── Global environment ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<EnvironmentModel> LoadGlobalEnvironmentAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        var filePath = GetGlobalFilePath(collectionFolderPath);
        if (!File.Exists(filePath))
            return new EnvironmentModel { FilePath = filePath, Name = "Global", Variables = [] };

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
            Path.GetFileNameWithoutExtension(filePath),
            GlobalEnvironmentFileName,
            StringComparison.OrdinalIgnoreCase);

    private static string GetEnvFolder(string collectionFolderPath) =>
        Path.Combine(collectionFolderPath, FileSystemCollectionService.EnvironmentFolderName);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join('_', name.Split(invalid, StringSplitOptions.None));
    }

    private static EnvironmentModel DtoToModel(string filePath, EnvironmentDto dto) => new()
    {
        FilePath = filePath,
        Name = dto.Name ?? Path.GetFileNameWithoutExtension(filePath),
        Color = dto.Color,
        Variables = (dto.Variables ?? [])
            .Select(v => new EnvironmentVariable
            {
                Name = v.Name ?? string.Empty,
                Value = v.Value ?? string.Empty,
                VariableType = v.VariableType ?? EnvironmentVariable.VariableTypes.Static,
                IsSecret = v.IsSecret ?? false,
            })
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .ToList(),
    };

    private static EnvironmentDto ModelToDto(EnvironmentModel model) => new()
    {
        Name = model.Name,
        Color = model.Color,
        Variables = model.Variables
            .Select(v => new VariableDto
            {
                Name = v.Name,
                Value = v.Value,
                VariableType = v.VariableType == EnvironmentVariable.VariableTypes.Static
                    ? null  // omit the default to keep JSON tidy
                    : v.VariableType,
                IsSecret = v.IsSecret ? (bool?)true : null,
            })
            .ToList(),
    };

    // ─── Private DTOs (JSON shape) ──────────────────────────────────────────

    private sealed class EnvironmentDto
    {
        public string? Name { get; init; }
        public string? Color { get; init; }
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
    }
}
