using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.GlobalEnvironmentEditorViewModel"/> when the global
/// environment is loaded or saved. Received by <see cref="ViewModels.RequestEditorViewModel"/>
/// so that all open tabs use the latest global variable values.
/// </summary>
public sealed class GlobalEnvironmentChangedMessage(IReadOnlyList<EnvironmentVariable> variables)
    : ValueChangedMessage<IReadOnlyList<EnvironmentVariable>>(variables);
