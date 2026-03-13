using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Published by <see cref="ViewModels.EnvironmentViewModel"/> when the active environment changes.
/// Received by <see cref="ViewModels.RequestViewModel"/> to apply variable substitution.
/// A <see langword="null"/> value means no environment is active.
/// </summary>
public sealed class EnvironmentChangedMessage(EnvironmentModel? environment)
    : ValueChangedMessage<EnvironmentModel?>(environment);
