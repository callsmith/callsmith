using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Callsmith.Desktop.Messages;
using Microsoft.Extensions.Logging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the environment selector toolbar strip.
/// Maintains the list of environments available for the open collection and
/// tracks which one is currently active, broadcasting changes via
/// <see cref="EnvironmentChangedMessage"/>.
/// </summary>
public sealed partial class EnvironmentViewModel : ObservableRecipient,
    IRecipient<CollectionOpenedMessage>
{
    private readonly IEnvironmentService _environmentService;
    private readonly ILogger<EnvironmentViewModel> _logger;

    private string? _collectionFolderPath;

    [ObservableProperty]
    private IReadOnlyList<EnvironmentModel> _environments = [];

    [ObservableProperty]
    private EnvironmentModel? _activeEnvironment;

    public EnvironmentViewModel(
        IEnvironmentService environmentService,
        IMessenger messenger,
        ILogger<EnvironmentViewModel> logger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(environmentService);
        ArgumentNullException.ThrowIfNull(logger);
        _environmentService = environmentService;
        _logger = logger;
        IsActive = true;
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Sets <paramref name="environment"/> as the active environment.</summary>
    [RelayCommand]
    private void SetActive(EnvironmentModel? environment)
    {
        ActiveEnvironment = environment;
    }

    // ─── Message handlers ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Receive(CollectionOpenedMessage message)
    {
        _collectionFolderPath = message.Value;
        _ = LoadEnvironmentsAsync(message.Value);
    }

    // ─── Property change side-effects ─────────────────────────────────────────

    partial void OnActiveEnvironmentChanged(EnvironmentModel? value)
    {
        Messenger.Send(new EnvironmentChangedMessage(value));
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private async Task LoadEnvironmentsAsync(string collectionFolderPath)
    {
        try
        {
            var list = await _environmentService
                .ListEnvironmentsAsync(collectionFolderPath)
                .ConfigureAwait(true);   // resume on UI thread

            Environments = list;

            // Retain the current selection if it still exists; otherwise clear it.
            if (ActiveEnvironment is not null &&
                !list.Any(e => e.FilePath == ActiveEnvironment.FilePath))
            {
                ActiveEnvironment = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load environments for '{Path}'", collectionFolderPath);
        }
    }
}
