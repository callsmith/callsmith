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
/// Also manages the visibility of the full <see cref="EnvironmentEditorViewModel"/>
/// panel via <see cref="IsEditorOpen"/>.
/// </summary>
public sealed partial class EnvironmentViewModel : ObservableRecipient,
    IRecipient<CollectionOpenedMessage>,
    IRecipient<EnvironmentSavedMessage>
{
    private readonly IEnvironmentService _environmentService;
    private readonly ICollectionPreferencesService _preferencesService;
    private readonly ILogger<EnvironmentViewModel> _logger;

    private string? _collectionFolderPath;

    [ObservableProperty]
    private IReadOnlyList<EnvironmentModel> _environments = [];

    [ObservableProperty]
    private EnvironmentModel? _activeEnvironment;

    /// <summary>
    /// True when the environment editor panel is open (replacing the request editor in the right pane).
    /// </summary>
    [ObservableProperty]
    private bool _isEditorOpen;

    public EnvironmentViewModel(
        IEnvironmentService environmentService,
        ICollectionPreferencesService preferencesService,
        IMessenger messenger,
        ILogger<EnvironmentViewModel> logger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(environmentService);
        ArgumentNullException.ThrowIfNull(preferencesService);
        ArgumentNullException.ThrowIfNull(logger);
        _environmentService = environmentService;
        _preferencesService = preferencesService;
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

    /// <summary>Opens the environment editor panel in the right pane.</summary>
    [RelayCommand]
    private void OpenEditor()
    {
        IsEditorOpen = true;
    }

    /// <summary>Closes the environment editor panel and returns to the request editor.</summary>
    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
    }

    // ─── Message handlers ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Receive(CollectionOpenedMessage message)
    {
        _collectionFolderPath = message.Value;
        _ = LoadEnvironmentsAsync(message.Value);
    }

    /// <summary>
    /// When an environment is saved in the editor, refresh the environments list
    /// and update the active environment model if it is the one that was saved.
    /// </summary>
    public void Receive(EnvironmentSavedMessage message)
    {
        _ = LoadEnvironmentsAsync(_collectionFolderPath);

        // If the saved environment is the currently active one, refresh its variables.
        if (ActiveEnvironment is not null &&
            string.Equals(ActiveEnvironment.FilePath, message.Value.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ActiveEnvironment = message.Value;
        }
    }

    // ─── Property change side-effects ─────────────────────────────────────────

    partial void OnActiveEnvironmentChanged(EnvironmentModel? value)
    {
        Messenger.Send(new EnvironmentChangedMessage(value));

        // Persist the selection so it can be restored on next launch.
        if (_collectionFolderPath is not null)
            _ = PersistActiveEnvironmentAsync(value?.FilePath);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private async Task LoadEnvironmentsAsync(string? collectionFolderPath)
    {
        if (collectionFolderPath is null)
            return;

        try
        {
            // Load both the environment list and the saved preference in one background hop
            // so that setting Environments and restoring the selection happen in a single
            // synchronous block on the UI thread — no yield between them.
            var (list, prefs) = await Task.Run(async () =>
            {
                var envs = await _environmentService
                    .ListEnvironmentsAsync(collectionFolderPath)
                    .ConfigureAwait(false);
                var p = await _preferencesService
                    .LoadAsync(collectionFolderPath)
                    .ConfigureAwait(false);
                return (envs, p);
            }).ConfigureAwait(true);   // resume on UI thread

            Environments = list;

            // Retain the current selection if it still exists; otherwise clear it.
            if (ActiveEnvironment is not null &&
                !list.Any(e => e.FilePath == ActiveEnvironment.FilePath))
            {
                ActiveEnvironment = null;
            }

            // If nothing is selected, restore the last-used environment.
            if (ActiveEnvironment is null && prefs.LastActiveEnvironmentFile is not null)
            {
                // Stored value is relative to the collection folder; expand before comparing.
                var absoluteEnvPath = Path.GetFullPath(
                    Path.Combine(collectionFolderPath, prefs.LastActiveEnvironmentFile));
                ActiveEnvironment = list.FirstOrDefault(e =>
                    string.Equals(e.FilePath, absoluteEnvPath,
                        StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load environments for '{Path}'", collectionFolderPath);
        }
    }

    private async Task PersistActiveEnvironmentAsync(string? filePath)
    {
        if (_collectionFolderPath is null) return;
        try
        {
            // Store relative to the collection folder so the pref is robust if the
            // collection root is renamed but the layout inside is preserved.
            var relativeFilePath = filePath is not null
                ? Path.GetRelativePath(_collectionFolderPath, filePath)
                : null;

            // Read-modify-write: preserve any prefs owned by other ViewModels (e.g. open tabs).
            var current = await _preferencesService.LoadAsync(_collectionFolderPath).ConfigureAwait(false);
            await _preferencesService
                .SaveAsync(_collectionFolderPath, new()
                {
                    LastActiveEnvironmentFile = relativeFilePath,
                    OpenTabPaths = current.OpenTabPaths,
                    ActiveTabPath = current.ActiveTabPath,
                })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist active environment preference");
        }
    }
}
