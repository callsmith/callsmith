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

    // ─── Import-type options ─────────────────────────────────────────────────

    private const string OpenApiImportTypeName = "Open API 3.x / Swagger 2.0";

    /// <summary>
    /// All import types shown in the dropdown.
    /// Hoppscotch remains disabled until its importer is implemented.
    /// </summary>
    public IReadOnlyList<ImportTypeOption> ImportTypeOptions { get; } =
    [
        new("Postman",                    isEnabled: true),
        new("Insomnia",                   isEnabled: true),
        new(OpenApiImportTypeName,        isEnabled: true),
        new("Hoppscotch",                 isEnabled: false),
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

        OnPropertyChanged(nameof(IsOpenApiSelected));
        ImportCommand.NotifyCanExecuteChanged();

        // Switching away from Open API clears the URL field to avoid stale state.
        if (!IsOpenApiSelected)
            SpecUrl = string.Empty;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _filePath = string.Empty;

    partial void OnFilePathChanged(string value)
    {
        // Mutual exclusivity: selecting a file clears any typed URL.
        if (!string.IsNullOrEmpty(value))
            SpecUrl = string.Empty;

        OnPropertyChanged(nameof(IsFileInputEnabled));
        OnPropertyChanged(nameof(IsUrlInputEnabled));
    }

    /// <summary>
    /// URL of a publicly accessible OpenAPI / Swagger spec to fetch.
    /// Only used when <see cref="IsOpenApiSelected"/> is true.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _specUrl = string.Empty;

    partial void OnSpecUrlChanged(string value)
    {
        // Mutual exclusivity: typing a URL clears any selected file.
        if (!string.IsNullOrEmpty(value))
            FilePath = string.Empty;

        OnPropertyChanged(nameof(IsFileInputEnabled));
        OnPropertyChanged(nameof(IsUrlInputEnabled));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isNonEmptyFolderWarningVisible;

    [ObservableProperty]
    private bool _isImporting;

    /// <summary>True when the selected import type is "Open API 3.x / Swagger 2.0".</summary>
    public bool IsOpenApiSelected =>
        SelectedImportType.Name.Equals(OpenApiImportTypeName, StringComparison.Ordinal);

    /// <summary>
    /// True when the file-browse row should be interactive (no URL has been entered yet).
    /// False when the user has typed a URL, disabling the file row.
    /// </summary>
    public bool IsFileInputEnabled => string.IsNullOrWhiteSpace(SpecUrl);

    /// <summary>
    /// True when the URL row should be interactive (no file has been selected yet).
    /// False when the user has selected a file, disabling the URL row.
    /// Only relevant when <see cref="IsOpenApiSelected"/> is true.
    /// </summary>
    public bool IsUrlInputEnabled => string.IsNullOrWhiteSpace(FilePath);

    // ─── Result ───────────────────────────────────────────────────────────────

    /// <summary>True after the import completes successfully.</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>The destination folder path, available after a successful import.</summary>
    public string ResultFolderPath { get; private set; } = string.Empty;

    // ─── Close signal ─────────────────────────────────────────────────────────

    /// <summary>Raised when the dialog should close (import succeeded or user cancelled).</summary>
    public event EventHandler? CloseRequested;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public ImportCollectionViewModel(ICollectionImportService importService)
    {
        ArgumentNullException.ThrowIfNull(importService);
        _importService = importService;
        _selectedImportType = ImportTypeOptions[0]; // default to Insomnia
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
    /// Clears the selected file path, re-enabling the URL input for OpenAPI imports.
    /// </summary>
    [RelayCommand]
    private void ClearFilePath()
    {
        FilePath = string.Empty;
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

    private bool CanImport
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FolderPath)) return false;
            if (IsOpenApiSelected)
                return !string.IsNullOrWhiteSpace(FilePath) || !string.IsNullOrWhiteSpace(SpecUrl);
            return !string.IsNullOrWhiteSpace(FilePath);
        }
    }

    private async Task RunImportAsync(CancellationToken ct)
    {
        IsImporting = true;
        ErrorMessage = string.Empty;

        try
        {
            if (IsOpenApiSelected && !string.IsNullOrWhiteSpace(SpecUrl))
                await _importService.ImportFromUrlToFolderAsync(SpecUrl, FolderPath, ct);
            else
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
