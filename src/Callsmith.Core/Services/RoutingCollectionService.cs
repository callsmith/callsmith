using Callsmith.Core.Abstractions;
using Callsmith.Core.Bruno;
using Callsmith.Core.Models;

namespace Callsmith.Core.Services;

/// <summary>
/// <see cref="ICollectionService"/> that transparently routes each operation to either
/// <see cref="BrunoCollectionService"/> or <see cref="FileSystemCollectionService"/>
/// based on the collection type at the path being operated on.
/// <para>
/// Detection strategy:
/// <list type="bullet">
///   <item><b>Open:</b> checks for <c>bruno.json</c> in the root folder.</item>
///   <item><b>File operations:</b> detects from the <c>.bru</c> / <c>.callsmith</c> file extension.</item>
///   <item><b>Folder operations:</b> walks up the directory tree to find <c>bruno.json</c>.</item>
/// </list>
/// After every <see cref="OpenFolderAsync"/> call the result is cached so folder
/// operations within the same collection are resolved without touching the filesystem again.
/// </para>
/// </summary>
public sealed class RoutingCollectionService : ICollectionService
{
    private readonly BrunoCollectionService _brunoService;
    private readonly FileSystemCollectionService _callsmithService;

    private bool _isBruno;
    private string _currentRoot = string.Empty;

    public RoutingCollectionService(
        BrunoCollectionService brunoService,
        FileSystemCollectionService callsmithService)
    {
        ArgumentNullException.ThrowIfNull(brunoService);
        ArgumentNullException.ThrowIfNull(callsmithService);
        _brunoService = brunoService;
        _callsmithService = callsmithService;
    }

    public string RequestFileExtension =>
        _isBruno
            ? BrunoCollectionService.RequestFileExtension
            : FileSystemCollectionService.RequestFileExtension;

    // ─────────────────────────────────────────────────────────────────────────
    //  Open — sets the current-collection context
    // ─────────────────────────────────────────────────────────────────────────

    public Task<CollectionFolder> OpenFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        _isBruno = BrunoDetector.IsBrunoCollection(folderPath);
        _currentRoot = Path.GetFullPath(folderPath);
        return Active.OpenFolderAsync(folderPath, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  File-level operations — route by extension
    // ─────────────────────────────────────────────────────────────────────────

    public Task<CollectionRequest> LoadRequestAsync(string filePath, CancellationToken ct = default) =>
        ServiceForFile(filePath).LoadRequestAsync(filePath, ct);

    public Task SaveRequestAsync(CollectionRequest request, CancellationToken ct = default) =>
        ServiceForFile(request.FilePath).SaveRequestAsync(request, ct);

    public Task DeleteRequestAsync(string filePath, CancellationToken ct = default) =>
        ServiceForFile(filePath).DeleteRequestAsync(filePath, ct);

    public Task<CollectionRequest> RenameRequestAsync(string filePath, string newName, CancellationToken ct = default) =>
        ServiceForFile(filePath).RenameRequestAsync(filePath, newName, ct);

    public Task<CollectionRequest> MoveRequestAsync(string filePath, string destinationFolderPath, CancellationToken ct = default) =>
        ServiceForFile(filePath).MoveRequestAsync(filePath, destinationFolderPath, ct);

    // ─────────────────────────────────────────────────────────────────────────
    //  Create operations — route by folder context
    // ─────────────────────────────────────────────────────────────────────────

    public Task<CollectionRequest> CreateRequestAsync(string folderPath, string name, CancellationToken ct = default) =>
        ServiceForFolder(folderPath).CreateRequestAsync(folderPath, name, ct);

    public Task<CollectionFolder> CreateFolderAsync(string parentPath, string name, CancellationToken ct = default) =>
        ServiceForFolder(parentPath).CreateFolderAsync(parentPath, name, ct);

    public Task<CollectionFolder> RenameFolderAsync(string folderPath, string newName, CancellationToken ct = default) =>
        ServiceForFolder(folderPath).RenameFolderAsync(folderPath, newName, ct);

    public Task DeleteFolderAsync(string folderPath, CancellationToken ct = default) =>
        ServiceForFolder(folderPath).DeleteFolderAsync(folderPath, ct);

    public Task SaveFolderOrderAsync(string folderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default) =>
        ServiceForFolder(folderPath).SaveFolderOrderAsync(folderPath, orderedNames, ct);

    // ─────────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private ICollectionService Active => _isBruno ? _brunoService : _callsmithService;

    private ICollectionService ServiceForFile(string filePath) =>
        BrunoDetector.IsBrunoFile(filePath) ? _brunoService : _callsmithService;

    private ICollectionService ServiceForFolder(string folderPath)
    {
        // Fast path: check against the cached root first.
        if (!string.IsNullOrEmpty(_currentRoot))
        {
            var fullPath = Path.GetFullPath(folderPath);
            if (fullPath.StartsWith(_currentRoot, StringComparison.OrdinalIgnoreCase))
                return Active;
        }

        // Slow path: walk the directory tree to find bruno.json.
        return BrunoDetector.IsUnderBrunoCollection(folderPath)
            ? _brunoService
            : _callsmithService;
    }
}
