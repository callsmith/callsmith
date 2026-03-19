using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.EnvironmentEditorViewModel"/> when the global
/// environment is loaded or saved. Received by <see cref="ViewModels.RequestEditorViewModel"/>
/// so that all open tabs use the latest global variable values.
/// The full <see cref="EnvironmentModel"/> is carried so that the file path is available
/// for dynamic-variable cache keying.
/// </summary>
public sealed class GlobalEnvironmentChangedMessage(EnvironmentModel environment)
    : ValueChangedMessage<EnvironmentModel>(environment);
