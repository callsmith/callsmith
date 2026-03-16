using System.Collections.ObjectModel;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using Callsmith.Desktop.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the full environment editor panel.
/// Allows the user to create, rename, and delete environments, and to manage the
/// variables within each environment (including secret variables).
///
/// Activated (and refreshed) whenever a <see cref="CollectionOpenedMessage"/> arrives.
/// Sends a <see cref="EnvironmentChangedMessage"/> after saving so the active
/// environment in the request editor stays up-to-date.
/// </summary>
public sealed partial class EnvironmentEditorViewModel : ObservableRecipient,
    IRecipient<CollectionOpenedMessage>
{
    private readonly IEnvironmentService _environmentService;
    private readonly ILogger<EnvironmentEditorViewModel> _logger;

    private string? _collectionFolderPath;

    // ─── Observable state ────────────────────────────────────────────────────

    /// <summary>All environments found in the open collection.</summary>
    public ObservableCollection<EnvironmentListItemViewModel> Environments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    private EnvironmentListItemViewModel? _selectedEnvironment;

    /// <summary>True while a new-environment input row is being shown.</summary>
    [ObservableProperty]
    private bool _isAddingEnvironment;

    /// <summary>Draft name for the new environment.</summary>
    [ObservableProperty]
    private string _newEnvironmentName = string.Empty;

    /// <summary>Validation message for the new-environment name field.</summary>
    [ObservableProperty]
    private string _newEnvironmentError = string.Empty;

    /// <summary>True while any async operation is in progress.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Transient error message for display in the editor header.</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public EnvironmentEditorViewModel(
        IEnvironmentService environmentService,
        IMessenger messenger,
        ILogger<EnvironmentEditorViewModel> logger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(environmentService);
        ArgumentNullException.ThrowIfNull(logger);
        _environmentService = environmentService;
        _logger = logger;
        IsActive = true;
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Shows the inline new-environment input row.</summary>
    [RelayCommand]
    private void BeginAddEnvironment()
    {
        NewEnvironmentName = string.Empty;
        NewEnvironmentError = string.Empty;
        IsAddingEnvironment = true;
    }

    /// <summary>Cancels the new-environment input row without creating anything.</summary>
    [RelayCommand]
    private void CancelAddEnvironment()
    {
        NewEnvironmentName = string.Empty;
        NewEnvironmentError = string.Empty;
        IsAddingEnvironment = false;
    }

    /// <summary>Creates the new environment on disk and adds it to the list.</summary>
    [RelayCommand]
    private async Task CommitAddEnvironmentAsync(CancellationToken ct)
    {
        if (_collectionFolderPath is null)
            return;

        var trimmed = NewEnvironmentName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            NewEnvironmentError = "Name cannot be empty.";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var model = await _environmentService
                .CreateEnvironmentAsync(_collectionFolderPath, trimmed, ct)
                .ConfigureAwait(true);

            var item = CreateListItem(model);
            Environments.Add(item);
            SelectedEnvironment = item;

            IsAddingEnvironment = false;
            NewEnvironmentName = string.Empty;
            NewEnvironmentError = string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            NewEnvironmentError = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create environment '{Name}'", trimmed);
            ErrorMessage = "Failed to create environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes the currently selected environment after confirmation.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedEnvironment))]
    private async Task DeleteEnvironmentAsync(CancellationToken ct)
    {
        if (SelectedEnvironment is null)
            return;

        var targetName = SelectedEnvironment.Name;
        var targetPath = SelectedEnvironment.FilePath;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _environmentService
                .DeleteEnvironmentAsync(targetPath, ct)
                .ConfigureAwait(true);

            var toRemove = Environments.FirstOrDefault(e => e.FilePath == targetPath);
            if (toRemove is not null)
                Environments.Remove(toRemove);

            SelectedEnvironment = Environments.Count > 0 ? Environments[0] : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete environment '{Name}'", targetName);
            ErrorMessage = "Failed to delete environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Saves variable changes for the currently selected environment to disk.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedEnvironment))]
    private async Task SaveSelectedAsync(CancellationToken ct)
    {
        if (SelectedEnvironment is null)
            return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var model = SelectedEnvironment.BuildModel();
            await _environmentService.SaveEnvironmentAsync(model, ct).ConfigureAwait(true);
            SelectedEnvironment.IsDirty = false;

            // Notify the request editor so variable substitution uses the updated values.
            Messenger.Send(new EnvironmentSavedMessage(model));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save environment '{Name}'", SelectedEnvironment?.Name);
            ErrorMessage = "Failed to save environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Message handlers ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Receive(CollectionOpenedMessage message)
    {
        _collectionFolderPath = message.Value;
        _ = LoadEnvironmentsAsync(message.Value);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private bool HasSelectedEnvironment => SelectedEnvironment is not null;

    private async Task LoadEnvironmentsAsync(string collectionFolderPath)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var list = await _environmentService
                .ListEnvironmentsAsync(collectionFolderPath)
                .ConfigureAwait(true);

            Environments.Clear();
            foreach (var model in list)
                Environments.Add(CreateListItem(model));

            SelectedEnvironment = Environments.Count > 0 ? Environments[0] : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load environments for '{Path}'", collectionFolderPath);
            ErrorMessage = "Failed to load environments.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private EnvironmentListItemViewModel CreateListItem(EnvironmentModel model)
        => new(
            model,
            onRenameCommit: RenameAsync,
            onDeleteRequest: async (item, ct) =>
            {
                SelectedEnvironment = item;
                await DeleteEnvironmentAsync(ct).ConfigureAwait(true);
            });

    private async Task RenameAsync(
        EnvironmentListItemViewModel item, string newName, CancellationToken ct)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var renamedModel = await _environmentService
                .RenameEnvironmentAsync(item.FilePath, newName, ct)
                .ConfigureAwait(true);

            item.ApplyRename(renamedModel);
        }
        catch (InvalidOperationException ex)
        {
            item.RenameError = ex.Message;
            item.IsRenaming = true;   // reopen rename box to let the user correct the input
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename environment '{Name}' → '{NewName}'", item.Name, newName);
            ErrorMessage = "Failed to rename environment. Check logs for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
