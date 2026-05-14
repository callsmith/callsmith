using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Manages sequence files stored in the <c>sequences/</c> sub-folder of a collection.
/// Each sequence is a <c>.seq.callsmith</c> JSON file containing an ordered list of steps.
/// </summary>
public interface ISequenceService
{
    /// <summary>
    /// Returns all sequences found in the <c>sequences/</c> sub-folder of
    /// <paramref name="collectionFolderPath"/>. Returns an empty list when the folder
    /// does not exist or contains no valid sequence files.
    /// </summary>
    Task<IReadOnlyList<SequenceModel>> ListSequencesAsync(
        string collectionFolderPath, CancellationToken ct = default);

    /// <summary>Loads a single sequence from disk by its absolute file path.</summary>
    Task<SequenceModel> LoadSequenceAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Saves a sequence to disk. Overwrites the existing file if it exists.
    /// Creates the <c>sequences/</c> directory if it does not exist.
    /// </summary>
    Task SaveSequenceAsync(SequenceModel sequence, CancellationToken ct = default);

    /// <summary>
    /// Creates a new empty sequence file in the <c>sequences/</c> sub-folder of
    /// <paramref name="collectionFolderPath"/> and returns the resulting model.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a sequence with the same name already exists.
    /// </exception>
    Task<SequenceModel> CreateSequenceAsync(
        string collectionFolderPath, string name, CancellationToken ct = default);

    /// <summary>Deletes the sequence file at <paramref name="filePath"/>.</summary>
    Task DeleteSequenceAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Renames a sequence file and returns the updated model with the new name and path.
    /// </summary>
    Task<SequenceModel> RenameSequenceAsync(
        string filePath, string newName, CancellationToken ct = default);
}
