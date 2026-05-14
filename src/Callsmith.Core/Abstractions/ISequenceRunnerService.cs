using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Executes a <see cref="SequenceModel"/> step by step, assembling and sending each
/// request, extracting variables from responses, and injecting them into the runtime
/// environment so that subsequent steps can consume them.
/// </summary>
public interface ISequenceRunnerService
{
    /// <summary>
    /// Runs all steps in <paramref name="sequence"/> in order, returning a
    /// <see cref="SequenceRunResult"/> that captures each step's outcome.
    /// </summary>
    /// <param name="sequence">The sequence to execute.</param>
    /// <param name="globalEnvironment">Global environment variables (e.g. base URLs shared across all environments).</param>
    /// <param name="activeEnvironment">The currently selected collection environment, or <see langword="null"/>.</param>
    /// <param name="collectionRootPath">Absolute path of the collection root folder (used for auth resolution).</param>
    /// <param name="progress">
    /// Optional callback invoked after each step completes, allowing the caller to display
    /// live progress without waiting for the full run to finish.
    /// </param>
    /// <param name="ct">Cancellation token. Cancelling mid-run stops execution after the current step.</param>
    Task<SequenceRunResult> RunAsync(
        SequenceModel sequence,
        EnvironmentModel globalEnvironment,
        EnvironmentModel? activeEnvironment,
        string collectionRootPath,
        IProgress<SequenceStepResult>? progress = null,
        CancellationToken ct = default);
}
