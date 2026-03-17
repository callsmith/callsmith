using Callsmith.Core.Abstractions;
using Callsmith.Core.Bruno;
using Callsmith.Core.Models;
using Microsoft.Extensions.Logging;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="IEnvironmentService"/> implementation that reads and writes Bruno environment
/// files located in the <c>environments/</c> sub-folder of a Bruno collection.
/// <para>
/// Each environment is a <c>&lt;Name&gt;.bru</c> file containing one or more of:
/// <list type="bullet">
///   <item><c>vars { key: value }</c> — regular (non-secret) variables</item>
///   <item><c>vars:secret { key: value }</c> — secret variables (not written to source-control by Bruno)</item>
/// </list>
/// </para>
/// </summary>
public sealed class BrunoEnvironmentService : IEnvironmentService
{
    public const string EnvironmentFolderName = "environments";
    public const string EnvironmentFileExtension = ".bru";
    public const string GlobalEnvironmentFileName = "_global";

    private readonly ILogger<BrunoEnvironmentService> _logger;

    public BrunoEnvironmentService(ILogger<BrunoEnvironmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
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

        var results = new List<EnvironmentModel>();
        foreach (var filePath in Directory.EnumerateFiles(envFolder, "*.bru", SearchOption.TopDirectoryOnly)
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
                _logger.LogWarning(ex, "Skipping unreadable Bruno environment file: {File}", filePath);
            }
        }

        results.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    public async Task<EnvironmentModel> LoadEnvironmentAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
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

        // vars:secret {} — secret variables (skip disabled ~ entries)
        if (doc.Find("vars:secret") is { } secretBlock)
        {
            foreach (var kv in secretBlock.Items.Where(kv => kv.IsEnabled))
                variables.Add(new EnvironmentVariable { Name = kv.Key, Value = kv.Value, IsSecret = true });
        }

        return new EnvironmentModel { FilePath = filePath, Name = name, Variables = variables };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Save / Create / Delete / Rename / Clone
    // ─────────────────────────────────────────────────────────────────────────

    public async Task SaveEnvironmentAsync(EnvironmentModel environment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var directory = Path.GetDirectoryName(environment.FilePath)!;
        Directory.CreateDirectory(directory);

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

        var model = new EnvironmentModel { FilePath = filePath, Name = name };
        await SaveEnvironmentAsync(model, ct).ConfigureAwait(false);
        return model;
    }

    public Task DeleteEnvironmentAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ct.ThrowIfCancellationRequested();

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogDebug("Deleted Bruno environment file: {Path}", filePath);
        }

        return Task.CompletedTask;
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
            File.Delete(filePath);

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
        var cloned = source with { FilePath = newFilePath, Name = newName };
        await SaveEnvironmentAsync(cloned, ct).ConfigureAwait(false);

        _logger.LogDebug("Cloned Bruno environment '{Source}' → '{New}'", sourceFilePath, newFilePath);
        return cloned;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildEnvContent(EnvironmentModel environment, BruDocument? existing)
    {
        var blocks = new List<BruBlock>();

        var regularVars = environment.Variables.Where(v => !v.IsSecret).ToList();
        var secretVars = environment.Variables.Where(v => v.IsSecret).ToList();

        // vars block (non-secret)
        var disabledVars = GetDisabledItems(existing, "vars");
        if (regularVars.Count > 0 || disabledVars.Count > 0)
        {
            var varsBlock = new BruBlock("vars");
            foreach (var v in regularVars)
                varsBlock.Items.Add(new BruKv(v.Name, v.Value));
            foreach (var kv in disabledVars)
                varsBlock.Items.Add(kv);
            blocks.Add(varsBlock);
        }

        // vars:secret block
        var disabledSecretVars = GetDisabledItems(existing, "vars:secret");
        if (secretVars.Count > 0 || disabledSecretVars.Count > 0)
        {
            var secretBlock = new BruBlock("vars:secret");
            foreach (var v in secretVars)
                secretBlock.Items.Add(new BruKv(v.Name, v.Value));
            foreach (var kv in disabledSecretVars)
                secretBlock.Items.Add(kv);
            blocks.Add(secretBlock);
        }

        // If there are no variables at all, emit an empty vars block so the file is valid.
        if (blocks.Count == 0)
        {
            blocks.Add(new BruBlock("vars"));
        }

        return BruWriter.Write(blocks);
    }

    private static IReadOnlyList<BruKv> GetDisabledItems(BruDocument? doc, string blockName) =>
        doc?.Find(blockName)?.Items.Where(kv => !kv.IsEnabled).ToList()
        ?? (IReadOnlyList<BruKv>)[];

    private static string GetEnvFolder(string collectionFolderPath) =>
        Path.Combine(collectionFolderPath, EnvironmentFolderName);

    // ─── Global environment ──────────────────────────────────────────────────

    public async Task<EnvironmentModel> LoadGlobalEnvironmentAsync(
        string collectionFolderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectionFolderPath);

        var filePath = Path.Combine(GetEnvFolder(collectionFolderPath),
            GlobalEnvironmentFileName + EnvironmentFileExtension);

        if (!File.Exists(filePath))
            return new EnvironmentModel { FilePath = filePath, Name = "Global", Variables = [] };

        return await LoadEnvironmentAsync(filePath, ct).ConfigureAwait(false);
    }

    public Task SaveGlobalEnvironmentAsync(
        EnvironmentModel globalEnvironment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(globalEnvironment);
        return SaveEnvironmentAsync(globalEnvironment, ct);
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
