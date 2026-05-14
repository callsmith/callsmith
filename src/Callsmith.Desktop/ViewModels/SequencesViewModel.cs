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
/// Drives the Sequences panel: listing sequences for the open collection,
/// creating/deleting sequences, and delegating editing/running to
/// <see cref="SequenceEditorViewModel"/>.
/// </summary>
public sealed partial class SequencesViewModel : ObservableRecipient,
    IRecipient<CollectionOpenedMessage>,
    IRecipient<EnvironmentChangedMessage>,
    IRecipient<GlobalEnvironmentChangedMessage>
{
    private readonly ISequenceService _sequenceService;
    private readonly ICollectionService _collectionService;
    private readonly SequenceEditorViewModel _editor;
    private readonly ILogger<SequencesViewModel> _logger;

    private string _collectionPath = string.Empty;

    private EnvironmentModel _globalEnvironment = new()
    {
        FilePath = string.Empty,
        EnvironmentId = Guid.NewGuid(),
        Name = "Global",
        Variables = [],
    };
    private EnvironmentModel? _activeEnvironment;

    // ─── Observable properties ────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSequences))]
    private ObservableCollection<SequenceListItemViewModel> _sequences = [];

    public bool HasSequences => Sequences.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSequence))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedSequenceCommand))]
    private SequenceListItemViewModel? _selectedSequence;

    public bool HasSelectedSequence => SelectedSequence is not null;

    /// <summary>The active sequence editor. Non-null when a sequence is selected.</summary>
    public SequenceEditorViewModel Editor => _editor;

    // ─── New-sequence creation state ─────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateNewSequence))]
    private string _newSequenceName = string.Empty;

    [ObservableProperty]
    private bool _isCreatingSequence;

    [ObservableProperty]
    private string? _createError;

    public bool CanCreateNewSequence =>
        !string.IsNullOrWhiteSpace(NewSequenceName) && !IsCreatingSequence;

    // ─── Constructor ──────────────────────────────────────────────────────────

    public SequencesViewModel(
        ISequenceService sequenceService,
        ICollectionService collectionService,
        SequenceEditorViewModel editor,
        IMessenger messenger,
        ILogger<SequencesViewModel> logger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(sequenceService);
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(logger);
        _sequenceService = sequenceService;
        _collectionService = collectionService;
        _editor = editor;
        _logger = logger;

        IsActive = true;
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Opens the sequences panel for the specified collection.</summary>
    public void Open(string collectionPath)
    {
        _collectionPath = collectionPath;
        IsOpen = true;
        _editor.UpdateEnvironment(_globalEnvironment, _activeEnvironment, collectionPath);
        _ = LoadSequencesAsync();
    }

    // ─── Message handlers ─────────────────────────────────────────────────────

    public void Receive(CollectionOpenedMessage message)
    {
        _collectionPath = message.Value;
        if (IsOpen)
            _ = LoadSequencesAsync();
    }

    public void Receive(EnvironmentChangedMessage message)
    {
        _activeEnvironment = message.Value;
        _editor.UpdateEnvironment(_globalEnvironment, _activeEnvironment, _collectionPath);
    }

    public void Receive(GlobalEnvironmentChangedMessage message)
    {
        _globalEnvironment = message.Value;
        _editor.UpdateEnvironment(_globalEnvironment, _activeEnvironment, _collectionPath);
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
    }

    [RelayCommand]
    private async Task CreateSequenceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(NewSequenceName) || string.IsNullOrWhiteSpace(_collectionPath))
            return;

        IsCreatingSequence = true;
        CreateError = null;
        try
        {
            var model = await _sequenceService
                .CreateSequenceAsync(_collectionPath, NewSequenceName.Trim(), ct)
                .ConfigureAwait(false);

            var item = new SequenceListItemViewModel(model.SequenceId, model.FilePath, model.Name);
            Sequences.Add(item);
            OnPropertyChanged(nameof(HasSequences));
            NewSequenceName = string.Empty;
            OpenSequence(item);
        }
        catch (InvalidOperationException ex)
        {
            CreateError = ex.Message;
        }
        catch (Exception ex)
        {
            CreateError = $"Failed to create sequence: {ex.Message}";
            _logger.LogError(ex, "Failed to create sequence");
        }
        finally
        {
            IsCreatingSequence = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSequence))]
    private async Task DeleteSelectedSequenceAsync(CancellationToken ct)
    {
        if (SelectedSequence is not { } item) return;

        try
        {
            await _sequenceService.DeleteSequenceAsync(item.FilePath, ct).ConfigureAwait(false);
            Sequences.Remove(item);
            OnPropertyChanged(nameof(HasSequences));

            if (SelectedSequence == item)
                SelectedSequence = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete sequence '{Name}'", item.Name);
            ErrorMessage = $"Failed to delete: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectSequence(SequenceListItemViewModel? item)
    {
        if (item is null) return;
        SelectedSequence = item;
        OpenSequence(item);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task LoadSequencesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_collectionPath)) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var models = await _sequenceService
                .ListSequencesAsync(_collectionPath, ct)
                .ConfigureAwait(false);

            var items = models
                .Select(m => new SequenceListItemViewModel(m.SequenceId, m.FilePath, m.Name))
                .ToList();

            Sequences = new ObservableCollection<SequenceListItemViewModel>(items);
            OnPropertyChanged(nameof(HasSequences));

            // Also load the flat request list for the editor's step picker.
            _ = LoadAvailableRequestsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sequences");
            ErrorMessage = $"Failed to load sequences: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadAvailableRequestsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_collectionPath)) return;

        try
        {
            var root = await _collectionService.OpenFolderAsync(_collectionPath, ct)
                .ConfigureAwait(false);
            var requests = FlattenRequests(root, _collectionPath);
            _editor.AvailableRequests = requests;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load available requests for sequence step picker");
        }
    }

    private void OpenSequence(SequenceListItemViewModel item)
    {
        SelectedSequence = item;
        _ = LoadAndOpenSequenceAsync(item);
    }

    private async Task LoadAndOpenSequenceAsync(SequenceListItemViewModel item, CancellationToken ct = default)
    {
        try
        {
            var model = await _sequenceService.LoadSequenceAsync(item.FilePath, ct).ConfigureAwait(false);
            _editor.LoadSequence(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sequence '{Name}'", item.Name);
            ErrorMessage = $"Failed to open sequence: {ex.Message}";
        }
    }

    private static IReadOnlyList<AvailableRequest> FlattenRequests(
        CollectionFolder folder, string collectionRoot)
    {
        var result = new List<AvailableRequest>();
        FlattenRecursive(folder, collectionRoot, result);
        return result;
    }

    private static void FlattenRecursive(
        CollectionFolder folder, string collectionRoot, List<AvailableRequest> result)
    {
        foreach (var req in folder.Requests)
        {
            var relative = Path.GetRelativePath(collectionRoot, req.FilePath)
                .Replace('\\', '/');
            // Strip the .callsmith extension for a cleaner display.
            if (relative.EndsWith(".callsmith", StringComparison.OrdinalIgnoreCase))
                relative = relative[..^".callsmith".Length];

            result.Add(new AvailableRequest
            {
                Name = req.Name,
                FilePath = req.FilePath,
                DisplayPath = relative,
            });
        }

        foreach (var sub in folder.SubFolders)
            FlattenRecursive(sub, collectionRoot, result);
    }
}

/// <summary>
/// A row in the sequences list panel.
/// </summary>
public sealed class SequenceListItemViewModel
{
    public Guid SequenceId { get; }
    public string FilePath { get; }
    public string Name { get; }

    public SequenceListItemViewModel(Guid sequenceId, string filePath, string name)
    {
        SequenceId = sequenceId;
        FilePath = filePath;
        Name = name;
    }
}
