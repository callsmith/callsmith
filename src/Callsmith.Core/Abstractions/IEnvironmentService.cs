using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Manages environment files stored in the <c>environment/</c> sub-folder of a collection.
/// Each environment is a JSON file containing a named set of variables.
/// </summary>
public interface IEnvironmentService
{
    /// <summary>
    /// Returns all environments found in the <c>environment/</c> sub-folder of
    /// <paramref name="collectionFolderPath"/>. Returns an empty list when the folder
    /// does not exist or contains no valid environment files.
    /// </summary>
    Task<IReadOnlyList<EnvironmentModel>> ListEnvironmentsAsync(
        string collectionFolderPath, CancellationToken ct = default);

    /// <summary>Loads a single environment from disk by its file path.</summary>
    Task<EnvironmentModel> LoadEnvironmentAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Saves an environment to disk. If the file already exists it is overwritten.
    /// Creates the <c>environment/</c> directory if it does not exist.
    /// </summary>
    Task SaveEnvironmentAsync(EnvironmentModel environment, CancellationToken ct = default);

    /// <summary>
    /// Creates a new empty environment file in the <c>environment/</c> sub-folder
    /// of <paramref name="collectionFolderPath"/> and returns the resulting model.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an environment with the same name already exists.
    /// </exception>
    Task<EnvironmentModel> CreateEnvironmentAsync(
        string collectionFolderPath, string name, CancellationToken ct = default);

    /// <summary>Deletes the environment file at <paramref name="filePath"/>.</summary>
    Task DeleteEnvironmentAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Renames an environment file and returns the updated model with the new name and path.
    /// </summary>
    Task<EnvironmentModel> RenameEnvironmentAsync(
        string filePath, string newName, CancellationToken ct = default);

    /// <summary>
    /// Creates a copy of the environment at <paramref name="sourceFilePath"/> with a new
    /// <paramref name="newName"/>, preserving all variables from the source.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an environment with the same name already exists.
    /// </exception>
    Task<EnvironmentModel> CloneEnvironmentAsync(
        string sourceFilePath, string newName, CancellationToken ct = default);

    /// <summary>
    /// Persists the user's preferred display order of environments to a <c>_meta.json</c>
    /// file stored inside the collection's environment sub-folder (alongside the env files).
    /// Pass an empty list to delete the meta file and restore the default alphabetical ordering.
    /// </summary>
    Task SaveEnvironmentOrderAsync(
        string collectionFolderPath, IReadOnlyList<string> orderedNames, CancellationToken ct = default);

    // ─── Global (collection-scoped) environment ───────────────────────────────

    /// <summary>
    /// Loads the global environment for the collection at <paramref name="collectionFolderPath"/>.
    /// The global environment is always active regardless of which collection environment is selected.
    /// Returns an empty environment model if the file does not yet exist.
    /// </summary>
    Task<EnvironmentModel> LoadGlobalEnvironmentAsync(
        string collectionFolderPath, CancellationToken ct = default);

    /// <summary>
    /// Saves the global environment for a collection, creating the backing file if needed.
    /// </summary>
    Task SaveGlobalEnvironmentAsync(
        EnvironmentModel globalEnvironment, CancellationToken ct = default);
}
