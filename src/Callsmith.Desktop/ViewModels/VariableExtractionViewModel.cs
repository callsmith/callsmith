using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for a single variable extraction rule within a <see cref="SequenceStepViewModel"/>.
/// Lets the user define a name, source (body/header), and expression to extract a value
/// from the step's response and inject it into the runtime environment.
/// </summary>
public sealed partial class VariableExtractionViewModel : ObservableObject
{
    private readonly Action<VariableExtractionViewModel> _requestRemove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodySource))]
    [NotifyPropertyChangedFor(nameof(IsHeaderSource))]
    [NotifyPropertyChangedFor(nameof(SourceIndex))]
    [NotifyPropertyChangedFor(nameof(ExpressionWatermark))]
    private VariableExtractionSource _source = VariableExtractionSource.ResponseBody;

    [ObservableProperty]
    private string _variableName = string.Empty;

    [ObservableProperty]
    private string _expression = string.Empty;

    public bool IsBodySource => Source == VariableExtractionSource.ResponseBody;
    public bool IsHeaderSource => Source == VariableExtractionSource.ResponseHeader;

    /// <summary>0 = ResponseBody, 1 = ResponseHeader. Convenience for ComboBox binding.</summary>
    public int SourceIndex
    {
        get => Source == VariableExtractionSource.ResponseBody ? 0 : 1;
        set => Source = value == 0 ? VariableExtractionSource.ResponseBody : VariableExtractionSource.ResponseHeader;
    }

    public string ExpressionWatermark => Source == VariableExtractionSource.ResponseBody
        ? "JSONPath expression (e.g. $.token)"
        : "Header name (e.g. Location)";

    public VariableExtractionViewModel(
        VariableExtraction extraction,
        Action<VariableExtractionViewModel> requestRemove)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(requestRemove);
        _requestRemove = requestRemove;
        _variableName = extraction.VariableName;
        _source = extraction.Source;
        _expression = extraction.Expression;
    }

    public VariableExtractionViewModel(Action<VariableExtractionViewModel> requestRemove)
    {
        ArgumentNullException.ThrowIfNull(requestRemove);
        _requestRemove = requestRemove;
    }

    [RelayCommand]
    private void Remove() => _requestRemove(this);

    /// <summary>Exports this VM's state as a domain model.</summary>
    public VariableExtraction ToModel() => new()
    {
        VariableName = VariableName.Trim(),
        Source = Source,
        Expression = Expression.Trim(),
    };
}
