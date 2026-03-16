using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.EnvironmentEditorViewModel"/> after an
/// environment's variables are successfully saved to disk.
/// Received by <see cref="ViewModels.EnvironmentViewModel"/> to refresh the active
/// environment's variable values so substitution uses the latest data.
/// </summary>
public sealed class EnvironmentSavedMessage(EnvironmentModel environment)
    : ValueChangedMessage<EnvironmentModel>(environment);
