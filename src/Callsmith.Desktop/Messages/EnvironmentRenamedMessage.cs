using Callsmith.Core.Models;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.EnvironmentEditorViewModel"/> after an environment
/// has been successfully renamed on disk.
/// Received by <see cref="ViewModels.EnvironmentViewModel"/> so it can update the active
/// environment reference (and persist the new path preference) when the active environment
/// is the one that was renamed.
/// </summary>
/// <param name="OldFilePath">The file path the environment had before the rename.</param>
/// <param name="RenamedModel">The environment model with its updated path and name.</param>
public sealed record EnvironmentRenamedMessage(string OldFilePath, EnvironmentModel RenamedModel);
