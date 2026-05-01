using Callsmith.Core.Abstractions;
using Callsmith.Core.Bruno;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="IEnvironmentService"/> that transparently routes each operation to either
/// <see cref="BrunoEnvironmentService"/> or <see cref="FileSystemEnvironmentService"/>
/// based on the collection type at the path being operated on.
/// </summary>
public sealed class RoutingEnvironmentService : IEnvironmentService
{
    private readonly BrunoEnvironmentService _brunoService;
    private readonly FileSystemEnvironmentService _callsmithService;

    public RoutingEnvironmentService(
        BrunoEnvironmentService brunoService,
        FileSystemEnvironmentService callsmithService)
    {
        ArgumentNullException.ThrowIfNull(brunoService);
        ArgumentNullException.ThrowIfNull(callsmithService);
        _brunoService = brunoService;
        _callsmithService = callsmithService;
    }

    // ListEnvironmentsAsync receives the collection folder path — detect by bruno.json presence.
    public Task<IReadOnlyList<EnvironmentModel>> ListEnvironmentsAsync(
        string collectionFolderPath, CancellationToken ct = default) =>
        BrunoDetector.IsBrunoCollection(collectionFolderPath)
            ? _brunoService.ListEnvironmentsAsync(collectionFolderPath, ct)
            : _callsmithService.ListEnvironmentsAsync(collectionFolderPath, ct);

    // All per-file operations can detect by file extension.
    public Task<EnvironmentModel> LoadEnvironmentAsync(string filePath, CancellationToken ct = default) =>
        ServiceForFile(filePath).LoadEnvironmentAsync(filePath, ct);

    public Task SaveEnvironmentsAsync(
        IReadOnlyList<EnvironmentModel> environments, CancellationToken ct = default)
    {
        if (environments.Count == 0) return Task.CompletedTask;
        return ServiceForFile(environments[0].FilePath).SaveEnvironmentsAsync(environments, ct);
    }

    public Task SaveEnvironmentAsync(EnvironmentModel environment, CancellationToken ct = default) =>
        ServiceForFile(environment.FilePath).SaveEnvironmentAsync(environment, ct);

    public Task<EnvironmentModel> CreateEnvironmentAsync(
        string collectionFolderPath, string name, CancellationToken ct = default) =>
        BrunoDetector.IsBrunoCollection(collectionFolderPath)
            ? _brunoService.CreateEnvironmentAsync(collectionFolderPath, name, ct)
            : _callsmithService.CreateEnvironmentAsync(collectionFolderPath, name, ct);

    public Task DeleteEnvironmentAsync(string filePath, CancellationToken ct = default) =>
        ServiceForFile(filePath).DeleteEnvironmentAsync(filePath, ct);

    public Task<EnvironmentModel> RenameEnvironmentAsync(string filePath, string newName, CancellationToken ct = default) =>
        ServiceForFile(filePath).RenameEnvironmentAsync(filePath, newName, ct);

    public Task<EnvironmentModel> CloneEnvironmentAsync(string sourceFilePath, string newName, CancellationToken ct = default) =>
        ServiceForFile(sourceFilePath).CloneEnvironmentAsync(sourceFilePath, newName, ct);

    public Task SaveEnvironmentOrderAsync(
        string collectionFolderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default) =>
        BrunoDetector.IsBrunoCollection(collectionFolderPath)
            ? _brunoService.SaveEnvironmentOrderAsync(collectionFolderPath, orderedNames, ct)
            : _callsmithService.SaveEnvironmentOrderAsync(collectionFolderPath, orderedNames, ct);

    public Task<EnvironmentModel> LoadGlobalEnvironmentAsync(
        string collectionFolderPath, CancellationToken ct = default) =>
        BrunoDetector.IsBrunoCollection(collectionFolderPath)
            ? _brunoService.LoadGlobalEnvironmentAsync(collectionFolderPath, ct)
            : _callsmithService.LoadGlobalEnvironmentAsync(collectionFolderPath, ct);

    public Task SaveGlobalEnvironmentAsync(
        EnvironmentModel globalEnvironment, CancellationToken ct = default) =>
        ServiceForFile(globalEnvironment.FilePath).SaveGlobalEnvironmentAsync(globalEnvironment, ct);

    private IEnvironmentService ServiceForFile(string filePath) =>
        BrunoDetector.IsBrunoFile(filePath) ? _brunoService : _callsmithService;
}
