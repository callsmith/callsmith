using System.Collections.ObjectModel;
using Callsmith.Core.MockData;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the mock data picker dialog.
/// Allows the user to browse Bogus categories and fields, preview a generated value,
/// and confirm a <see cref="MockDataSegment"/> to insert into an environment variable.
/// </summary>
public sealed partial class MockDataConfigViewModel : ObservableObject
{
    // ─── Available options ────────────────────────────────────────────────────

    /// <summary>All top-level category names in catalog order.</summary>
    public IReadOnlyList<string> Categories { get; } = MockDataCatalog.Categories;

    /// <summary>Fields for the currently selected category.</summary>
    public ObservableCollection<MockDataEntry> Fields { get; } = [];

    // ─── Bound fields ────────────────────────────────────────────────────────

    [ObservableProperty]
    private string? _selectedCategory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private MockDataEntry? _selectedField;

    [ObservableProperty]
    private string _previewValue = string.Empty;

    // ─── Result ───────────────────────────────────────────────────────────────

    /// <summary>True when the user has clicked OK.</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>The configured segment, available after the user confirms.</summary>
    public MockDataSegment? ResultSegment { get; private set; }

    // ─── Close signal ─────────────────────────────────────────────────────────

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public MockDataConfigViewModel(MockDataSegment? existing = null)
    {
        if (existing is not null)
        {
            // Pre-select the category and matching field from an existing segment.
            SelectedCategory = existing.Category;
            RebuildFields();
            SelectedField = Fields.FirstOrDefault(f => f.Field == existing.Field);
        }
        else
        {
            // Default to Internet → Email as a sensible starting point.
            SelectedCategory = Categories.Contains("Internet") ? "Internet" : Categories.FirstOrDefault();
            RebuildFields();
            SelectedField = Fields.FirstOrDefault(f => f.Field == "Email") ?? Fields.FirstOrDefault();
        }

        RegeneratePreview();
    }

    // ─── Property change hooks ────────────────────────────────────────────────

    partial void OnSelectedCategoryChanged(string? value)
    {
        RebuildFields();
        SelectedField = Fields.FirstOrDefault();
    }

    partial void OnSelectedFieldChanged(MockDataEntry? value)
    {
        RegeneratePreview();
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    /// <summary>Generates a fresh sample value for the current selection.</summary>
    [RelayCommand]
    private void Regenerate() => RegeneratePreview();

    /// <summary>Confirms the selection and signals the dialog to close.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (SelectedCategory is null || SelectedField is null) return;

        ResultSegment = new MockDataSegment
        {
            Category = SelectedCategory,
            Field = SelectedField.Field,
        };
        IsConfirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels without producing a result.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private bool CanConfirm => SelectedField is not null;

    private void RebuildFields()
    {
        Fields.Clear();
        if (SelectedCategory is null) return;
        foreach (var entry in MockDataCatalog.GetFields(SelectedCategory))
            Fields.Add(entry);
    }

    private void RegeneratePreview()
    {
        if (SelectedCategory is null || SelectedField is null)
        {
            PreviewValue = string.Empty;
            return;
        }
        PreviewValue = MockDataCatalog.Generate(SelectedCategory, SelectedField.Field);
    }
}
