using CommunityToolkit.Mvvm.Input;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// Represents a single downloadable multipart file part in the history detail panel.
/// </summary>
public sealed class HistoryMultipartFileViewModel
{
    /// <summary>Label shown in the download dropdown (typically the file name).</summary>
    public string DisplayName { get; }

    /// <summary>Command that saves the file to disk when executed.</summary>
    public IAsyncRelayCommand DownloadCommand { get; }

    public HistoryMultipartFileViewModel(string displayName, IAsyncRelayCommand downloadCommand)
    {
        DisplayName = displayName;
        DownloadCommand = downloadCommand;
    }
}
