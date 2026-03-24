using System.IO;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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
    IRecipient<EnvironmentSavedMessage>,
    IRecipient<EnvironmentOrderChangedMessage>,
    IRecipient<CloseEnvironmentEditorMessage>
{
    private readonly IEnvironmentService _environmentService;
    private readonly ICollectionPreferencesService _preferencesService;
    private readonly ILogger<EnvironmentViewModel> _logger;

    private string? _collectionFolderPath;

    /// <summary>
    /// Sentinel item that represents "no environment selected" in the dropdown.
    /// Using a static reference allows cheap identity comparison.
    /// </summary>
    private static readonly EnvironmentModel NoEnvironmentItem = new()
    {
        FilePath = string.Empty,
        Name = "(no environment)",
        Variables = [],
        EnvironmentId = Guid.NewGuid(),
    };

    [ObservableProperty]
    private IReadOnlyList<EnvironmentModel> _environments = [];

    [ObservableProperty]
    private EnvironmentModel? _activeEnvironment;

    /// <summary>
    /// The item currently selected in the environment ComboBox.
    /// Equals <see cref="NoEnvironmentItem"/> when no environment is active.
    /// </summary>
    [ObservableProperty]
    private EnvironmentModel _selectedDropdownItem = NoEnvironmentItem;

    /// <summary>
    /// Environments list for the dropdown, always starting with the
    /// <see cref="NoEnvironmentItem"/> sentinel so the user can deselect.
    /// </summary>
    public IReadOnlyList<EnvironmentModel> EnvironmentsWithPlaceholder
        => [NoEnvironmentItem, .. Environments];

    /// <summary>
    /// True when the environment editor panel is open (replacing the request editor in the right pane).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyEditorOpen))]
    private bool _isEditorOpen;

    /// <summary>
    /// True when either the collection environment editor or the global environment editor is open.
    /// Used by the main window to hide the normal collections sidebar and request editor.
    /// </summary>
    public bool IsAnyEditorOpen => IsEditorOpen;

    // Guards against circular updates between ActiveEnvironment ↔ SelectedDropdownItem.
    private bool _syncingSelection;

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

    /// <summary>Closes whichever editor panel is currently open and returns to the request editor.</summary>
    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
    }

    /// <inheritdoc/>
    public void Receive(CloseEnvironmentEditorMessage message) => IsEditorOpen = false;

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

    /// <summary>
    /// When the user reorders environments in the editor, reload to apply the new order
    /// to the dropdown as well.
    /// </summary>
    public void Receive(EnvironmentOrderChangedMessage message)
    {
        _ = LoadEnvironmentsAsync(_collectionFolderPath);
    }

    // ─── Property change side-effects ─────────────────────────────────────────

    partial void OnEnvironmentsChanged(IReadOnlyList<EnvironmentModel> value)
    {
        // Keep the dropdown list in sync whenever the environments collection changes.
        OnPropertyChanged(nameof(EnvironmentsWithPlaceholder));
    }

    partial void OnActiveEnvironmentChanged(EnvironmentModel? value)
    {
        // Keep the dropdown selection in sync without re-triggering OnSelectedDropdownItemChanged.
        if (!_syncingSelection)
        {
            _syncingSelection = true;
            SelectedDropdownItem = value ?? NoEnvironmentItem;
            _syncingSelection = false;
        }

        Messenger.Send(new EnvironmentChangedMessage(value));

        // Persist the selection so it can be restored on next launch.
        if (_collectionFolderPath is not null)
            _ = PersistActiveEnvironmentAsync(value?.FilePath);
    }

    partial void OnSelectedDropdownItemChanged(EnvironmentModel value)
    {
        // Map the sentinel back to null; keep ActiveEnvironment in sync.
        if (!_syncingSelection)
        {
            _syncingSelection = true;
            ActiveEnvironment = ReferenceEquals(value, NoEnvironmentItem) ? null : value;
            _syncingSelection = false;
        }
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
            // Always update to the fresh instance from the new list so the ComboBox can
            // match by reference (stale instances are never reference-equal to items in
            // the newly-loaded EnvironmentsWithPlaceholder).
            if (ActiveEnvironment is not null)
            {
                var refreshed = list.FirstOrDefault(e =>
                    string.Equals(e.FilePath, ActiveEnvironment.FilePath,
                        StringComparison.OrdinalIgnoreCase));
                ActiveEnvironment = refreshed; // null when no longer found; fresh ref otherwise
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

            await _preferencesService.UpdateAsync(_collectionFolderPath, current => new()
            {
                LastActiveEnvironmentFile = relativeFilePath,
                OpenTabPaths = current.OpenTabPaths,
                ActiveTabPath = current.ActiveTabPath,
                ExpandedFolderPaths = current.ExpandedFolderPaths,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist active environment preference");
        }
    }
}
