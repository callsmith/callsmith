using Avalonia.Platform.Storage;
using Callsmith.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the "Import collection from another API tool" dialog.
/// Owns the import-type selection, source-file and destination-folder paths,
/// the non-empty-folder warning, and the error message shown when parsing fails.
/// </summary>
public sealed partial class ImportCollectionViewModel : ObservableObject
{
    private readonly ICollectionImportService _importService;
    private readonly string? _currentCollectionPath;

    // ─── Import-type options ─────────────────────────────────────────────────

    /// <summary>
    /// All import types shown in the dropdown.
    /// Hoppscotch remains disabled until its importer is implemented.
    /// </summary>
    public IReadOnlyList<ImportTypeOption> ImportTypeOptions { get; } =
    [
        new("Postman",  isEnabled: true),
        new("Insomnia", isEnabled: true),
        new("Hoppscotch", isEnabled: false),
    ];

    // ─── Bound properties ────────────────────────────────────────────────────

    [ObservableProperty]
    private ImportTypeOption _selectedImportType;

    partial void OnSelectedImportTypeChanged(ImportTypeOption value)
    {
        // If the user selects a disabled entry (currently Hoppscotch), revert to the
        // first enabled option.
        if (!value.IsEnabled)
            SelectedImportType = ImportTypeOptions.First(o => o.IsEnabled);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _filePath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isNonEmptyFolderWarningVisible;

    [ObservableProperty]
    private bool _isImporting;

    /// <summary>
    /// When <c>true</c> the import merges into the currently open collection instead of
    /// creating a new one. Only settable when <see cref="HasCurrentCollectionOption"/> is true.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _isImportIntoCurrentCollection;

    partial void OnIsImportIntoCurrentCollectionChanged(bool value)
    {
        // Guard: this mode requires an open collection.
        if (value && !HasCurrentCollectionOption)
            IsImportIntoCurrentCollection = false;
    }

    /// <summary>
    /// Relative sub-folder path within the current collection where requests will be placed.
    /// Empty or whitespace means "collection root".
    /// Only relevant when <see cref="IsImportIntoCurrentCollection"/> is <c>true</c>.
    /// </summary>
    [ObservableProperty]
    private string _subFolderPath = string.Empty;

    // ─── Computed ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>true</c> when a collection is open and the "import into current collection"
    /// toggle should be shown.
    /// </summary>
    public bool HasCurrentCollectionOption =>
        !string.IsNullOrEmpty(_currentCollectionPath);

    // ─── Result ───────────────────────────────────────────────────────────────

    /// <summary>True after the import completes successfully.</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>
    /// True when the import was performed in "merge into current collection" mode.
    /// Available after a successful import.
    /// </summary>
    public bool ImportedIntoCurrentCollection { get; private set; }

    /// <summary>The destination folder path, available after a successful import.</summary>
    public string ResultFolderPath { get; private set; } = string.Empty;

    // ─── Close signal ─────────────────────────────────────────────────────────

    /// <summary>Raised when the dialog should close (import succeeded or user cancelled).</summary>
    public event EventHandler? CloseRequested;

    // ─── Constructor ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new import dialog ViewModel.
    /// </summary>
    /// <param name="importService">The import orchestration service.</param>
    /// <param name="currentCollectionPath">
    /// The root path of the currently open collection, or <c>null</c> when no
    /// collection is open.  When provided, the dialog shows a mode toggle that lets
    /// the user choose between creating a new collection and merging into the current one.
    /// </param>
    public ImportCollectionViewModel(
        ICollectionImportService importService,
        string? currentCollectionPath = null)
    {
        ArgumentNullException.ThrowIfNull(importService);
        _importService = importService;
        _currentCollectionPath = string.IsNullOrEmpty(currentCollectionPath)
            ? null
            : currentCollectionPath;
        _selectedImportType = ImportTypeOptions[0]; // default to Postman
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Opens a file picker so the user can choose the collection file to import.</summary>
    [RelayCommand]
    private async Task BrowseFileAsync(IStorageProvider storageProvider)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);

        var extensions = _importService.SupportedFileExtensions;
        var patterns = extensions.Select(e => $"*{e}").ToList();
        var fileTypeFilter = new FilePickerFileType("Collection Files")
        {
            Patterns = patterns,
        };

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Collection File to Import",
            AllowMultiple = false,
            FileTypeFilter = [fileTypeFilter, FilePickerFileTypes.All],
        });

        if (files is not [var file])
            return;

        var path = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            FilePath = path;
    }

    /// <summary>Opens a folder picker so the user can choose the destination folder.</summary>
    [RelayCommand]
    private async Task BrowseFolderAsync(IStorageProvider storageProvider)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Destination Folder for Import",
            AllowMultiple = false,
        });

        if (folders is not [var folder])
            return;

        var path = folder.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            FolderPath = path;
    }

    /// <summary>
    /// Validates inputs, warns if the destination folder is non-empty, then runs the import.
    /// Shows an inline error if the file cannot be parsed — the entire operation is a no-op.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync(CancellationToken ct)
    {
        ErrorMessage = string.Empty;
        IsNonEmptyFolderWarningVisible = false;

        if (IsImportIntoCurrentCollection)
        {
            // "Import into current collection" mode — no folder-emptiness check needed.
            await RunImportIntoCollectionAsync(ct);
            return;
        }

        // Guard: destination folder must not be empty on disk (warn first).
        if (IsFolderNonEmpty(FolderPath))
        {
            IsNonEmptyFolderWarningVisible = true;
            return;
        }

        await RunImportAsync(ct);
    }

    /// <summary>Proceeds with the import even though the destination folder is non-empty.</summary>
    [RelayCommand]
    private async Task ProceedAnywayAsync(CancellationToken ct)
    {
        IsNonEmptyFolderWarningVisible = false;
        await RunImportAsync(ct);
    }

    /// <summary>Dismisses the non-empty-folder warning without importing.</summary>
    [RelayCommand]
    private void CancelWarning()
    {
        IsNonEmptyFolderWarningVisible = false;
    }

    /// <summary>Closes the dialog without importing.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private bool CanImport =>
        !string.IsNullOrWhiteSpace(FilePath) &&
        (IsImportIntoCurrentCollection || !string.IsNullOrWhiteSpace(FolderPath));

    private async Task RunImportAsync(CancellationToken ct)
    {
        IsImporting = true;
        ErrorMessage = string.Empty;

        try
        {
            await _importService.ImportToFolderAsync(FilePath, FolderPath, ct);
            ResultFolderPath = FolderPath;
            IsConfirmed = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Silently cancelled — leave the dialog open.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    private async Task RunImportIntoCollectionAsync(CancellationToken ct)
    {
        IsImporting = true;
        ErrorMessage = string.Empty;

        try
        {
            // Validate SubFolderPath: must be relative (no rooted path, no ".." segments).
            if (!string.IsNullOrWhiteSpace(SubFolderPath))
            {
                if (Path.IsPathRooted(SubFolderPath) ||
                    SubFolderPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(seg => seg == ".."))
                {
                    ErrorMessage = "Sub-folder path must be a relative path with no '..' segments.";
                    return;
                }
            }

            var absoluteTarget = string.IsNullOrWhiteSpace(SubFolderPath)
                ? _currentCollectionPath!
                : Path.Combine(_currentCollectionPath!, SubFolderPath);

            await _importService.ImportIntoCollectionAsync(
                FilePath, _currentCollectionPath!, absoluteTarget, ct);

            ResultFolderPath = _currentCollectionPath!;
            ImportedIntoCurrentCollection = true;
            IsConfirmed = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Silently cancelled — leave the dialog open.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    private static bool IsFolderNonEmpty(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return false;

        return Directory.EnumerateFileSystemEntries(folderPath).Any();
    }

    // ─── Nested types ────────────────────────────────────────────────────────

    /// <summary>A display item for the import-type ComboBox.</summary>
    public sealed class ImportTypeOption(string name, bool isEnabled)
    {
        public string Name { get; } = name;
        public bool IsEnabled { get; } = isEnabled;
        public override string ToString() => Name;
    }
}
