using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.EnvironmentViewModel"/> when the user opens
/// the environment manager. Contains the active environment file path at open time.
/// A <see langword="null"/> value means "(no environment)" is selected.
/// </summary>
public sealed class OpenEnvironmentEditorMessage(string? activeEnvironmentFilePath)
    : ValueChangedMessage<string?>(activeEnvironmentFilePath);
