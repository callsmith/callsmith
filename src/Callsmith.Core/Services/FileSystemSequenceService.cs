using System.Text.Json;
using System.Text.Json.Serialization;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="ISequenceService"/> implementation that stores sequences as
/// <c>.seq.callsmith</c> JSON files in the <c>sequences/</c> sub-folder of a collection.
/// </summary>
public sealed class FileSystemSequenceService : ISequenceService
{
    /// <summary>File extension used for all sequence files.</summary>
    public const string SequenceFileExtension = ".seq.callsmith";

    /// <summary>
    /// Name of the sub-folder within a collection root that holds sequence files.
    /// </summary>
    public const string SequencesFolderName = "sequences";

    private readonly ILogger<FileSystemSequenceService> _logger;

    public FileSystemSequenceService(ILogger<FileSystemSequenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SequenceModel>> ListSequencesAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        var folder = GetSequencesFolder(collectionFolderPath);
        if (!Directory.Exists(folder))
            return [];

        var results = new List<SequenceModel>();
        foreach (var filePath in Directory.EnumerateFiles(
                     folder, $"*{SequenceFileExtension}", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var seq = await LoadSequenceAsync(filePath, ct).ConfigureAwait(false);
                results.Add(seq);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable sequence file: {File}", filePath);
            }
        }

        results.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <inheritdoc/>
    public async Task<SequenceModel> LoadSequenceAsync(
        string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Sequence file not found: '{filePath}'", filePath);

        await using var stream = File.OpenRead(filePath);
        var dto = await JsonSerializer
            .DeserializeAsync<SequenceFileDto>(stream, CallsmithJsonOptions.Default, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Sequence file is empty or null: '{filePath}'");

        return DtoToModel(filePath, dto);
    }

    /// <inheritdoc/>
    public async Task SaveSequenceAsync(SequenceModel sequence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var directory = Path.GetDirectoryName(sequence.FilePath)!;
        Directory.CreateDirectory(directory);

        var dto = ModelToDto(sequence);
        await WriteAtomicAsync(sequence.FilePath, dto, ct).ConfigureAwait(false);

        _logger.LogDebug("Saved sequence '{Name}' → {Path}", sequence.Name, sequence.FilePath);
    }

    /// <inheritdoc/>
    public async Task<SequenceModel> CreateSequenceAsync(
        string collectionFolderPath, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var folder = GetSequencesFolder(collectionFolderPath);
        var filePath = Path.Combine(folder, SanitizeFileName(name) + SequenceFileExtension);

        if (File.Exists(filePath))
            throw new InvalidOperationException(
                $"A sequence named '{name}' already exists in this collection.");

        var model = new SequenceModel
        {
            SequenceId = Guid.NewGuid(),
            FilePath = filePath,
            Name = name,
            Steps = [],
        };

        await SaveSequenceAsync(model, ct).ConfigureAwait(false);
        return model;
    }

    /// <inheritdoc/>
    public Task DeleteSequenceAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ct.ThrowIfCancellationRequested();

        if (File.Exists(filePath))
            File.Delete(filePath);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<SequenceModel> RenameSequenceAsync(
        string filePath, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var existing = await LoadSequenceAsync(filePath, ct).ConfigureAwait(false);
        var folder = Path.GetDirectoryName(filePath)!;
        var newFilePath = Path.Combine(folder, SanitizeFileName(newName) + SequenceFileExtension);

        var updated = existing with { Name = newName, FilePath = newFilePath };
        await SaveSequenceAsync(updated, ct).ConfigureAwait(false);

        if (!string.Equals(filePath, newFilePath, StringComparison.OrdinalIgnoreCase))
            File.Delete(filePath);

        return updated;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string GetSequencesFolder(string collectionFolderPath) =>
        Path.Combine(collectionFolderPath, SequencesFolderName);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join('_', name.Split(invalid, StringSplitOptions.None));
    }

    private static SequenceModel DtoToModel(string filePath, SequenceFileDto dto) => new()
    {
        SequenceId = dto.SequenceId == Guid.Empty ? Guid.NewGuid() : dto.SequenceId,
        FilePath = filePath,
        Name = dto.Name ?? Path.GetFileNameWithoutExtension(filePath),
        Steps = (dto.Steps ?? [])
            .Select(s => new SequenceStep
            {
                StepId = s.StepId == Guid.Empty ? Guid.NewGuid() : s.StepId,
                RequestFilePath = s.RequestFilePath ?? string.Empty,
                RequestName = s.RequestName ?? string.Empty,
                Extractions = (s.Extractions ?? [])
                    .Where(e => !string.IsNullOrWhiteSpace(e.VariableName))
                    .Select(e => new VariableExtraction
                    {
                        VariableName = e.VariableName!,
                        Source = e.Source,
                        Expression = e.Expression ?? string.Empty,
                    })
                    .ToList(),
            })
            .ToList(),
    };

    private static SequenceFileDto ModelToDto(SequenceModel model) => new()
    {
        SequenceId = model.SequenceId,
        Name = model.Name,
        Steps = model.Steps
            .Select(s => new SequenceStepDto
            {
                StepId = s.StepId,
                RequestFilePath = s.RequestFilePath,
                RequestName = s.RequestName,
                Extractions = s.Extractions
                    .Select(e => new VariableExtractionDto
                    {
                        VariableName = e.VariableName,
                        Source = e.Source,
                        Expression = e.Expression,
                    })
                    .ToList(),
            })
            .ToList(),
    };

    private static async Task WriteAtomicAsync(
        string filePath, SequenceFileDto dto, CancellationToken ct)
    {
        var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write))
                await JsonSerializer.SerializeAsync(stream, dto, CallsmithJsonOptions.Default, ct)
                    .ConfigureAwait(false);

            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    // ─── DTOs (on-disk representation) ───────────────────────────────────────

    private sealed class SequenceFileDto
    {
        [JsonPropertyName("sequenceId")]
        public Guid SequenceId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("steps")]
        public List<SequenceStepDto>? Steps { get; set; }
    }

    private sealed class SequenceStepDto
    {
        [JsonPropertyName("stepId")]
        public Guid StepId { get; set; }

        [JsonPropertyName("requestFilePath")]
        public string? RequestFilePath { get; set; }

        [JsonPropertyName("requestName")]
        public string? RequestName { get; set; }

        [JsonPropertyName("extractions")]
        public List<VariableExtractionDto>? Extractions { get; set; }
    }

    private sealed class VariableExtractionDto
    {
        [JsonPropertyName("variableName")]
        public string? VariableName { get; set; }

        [JsonPropertyName("source")]
        public VariableExtractionSource Source { get; set; }

        [JsonPropertyName("expression")]
        public string? Expression { get; set; }
    }
}
