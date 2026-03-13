using System.Net.Http;
using Callsmith.Core;
using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Callsmith.Desktop.Messages;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// ViewModel for the request editor and response viewer pane.
/// Receives a selected request from the sidebar via <see cref="RequestSelectedMessage"/>,
/// allows the user to edit and send it, and exposes the response for display.
/// </summary>
public sealed partial class RequestViewModel : ObservableRecipient, IRecipient<RequestSelectedMessage>
{
    private readonly TransportRegistry _transportRegistry;

    // -------------------------------------------------------------------------
    // Request editor state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private string _selectedMethod = "GET";

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _requestName = string.Empty;

    // -------------------------------------------------------------------------
    // Response state
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private ResponseModel? _response;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private string? _errorMessage;

    // -------------------------------------------------------------------------
    // Derived display properties
    // -------------------------------------------------------------------------

    /// <summary>HTTP status code as a display string, e.g. "200 OK".</summary>
    public string StatusDisplay =>
        Response is null ? string.Empty : $"{Response.StatusCode} {Response.ReasonPhrase}";

    /// <summary>Elapsed time formatted for display, e.g. "142 ms".</summary>
    public string ElapsedDisplay =>
        Response is null ? string.Empty : $"{Response.Elapsed.TotalMilliseconds:F0} ms";

    /// <summary>Response body size formatted for display, e.g. "1.2 KB".</summary>
    public string SizeDisplay => Response is null
        ? string.Empty
        : Response.BodySizeBytes switch
        {
            < 1024 => $"{Response.BodySizeBytes} B",
            < 1024 * 1024 => $"{Response.BodySizeBytes / 1024.0:F1} KB",
            _ => $"{Response.BodySizeBytes / (1024.0 * 1024):F1} MB",
        };

    /// <summary>CSS-style colour class for the status badge ("success", "redirect", "client-error", "server-error").</summary>
    public string StatusClass => Response?.StatusCode switch
    {
        >= 200 and < 300 => "success",
        >= 300 and < 400 => "redirect",
        >= 400 and < 500 => "client-error",
        >= 500 => "server-error",
        _ => string.Empty,
    };

    /// <summary>Available HTTP methods for the method selector.</summary>
    public IReadOnlyList<string> HttpMethods { get; } =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public RequestViewModel(TransportRegistry transportRegistry, IMessenger messenger)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(transportRegistry);
        _transportRegistry = transportRegistry;
        IsActive = true; // activate messenger registration
    }

    /// <summary>
    /// Receives a request selected in the collections sidebar and populates the editor.
    /// </summary>
    public void Receive(RequestSelectedMessage message)
    {
        var req = message.Value;
        RequestName = req.Name;
        SelectedMethod = req.Method.Method;
        Url = req.Url;
        Response = null;
        ErrorMessage = null;
    }

    /// <summary>Sends the current request and populates the response viewer.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Url))
            return;

        IsSending = true;
        Response = null;
        ErrorMessage = null;

        try
        {
            var request = new RequestModel
            {
                Method = new HttpMethod(SelectedMethod),
                Url = Url,
            };

            var transport = _transportRegistry.Resolve(request);
            Response = await transport.SendAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Request cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSending = false;
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(ElapsedDisplay));
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(StatusClass));
        }
    }
}
