using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// View model for the cURL command dialog.
/// Manages the auth-masking toggle and regenerates the command text on change.
/// </summary>
public sealed partial class CurlDialogViewModel : ObservableObject
{
    private readonly RequestModel _request;
    private readonly CurlAuthMaskInfo? _authMaskInfo;

    /// <summary>
    /// True when the request contains detectable authentication that can be masked.
    /// Drives the IsEnabled state of the "Include authentication" checkbox.
    /// </summary>
    public bool HasAuthentication { get; }

    /// <summary>
    /// When true, real credentials are shown in the cURL output.
    /// When false (default), they are replaced with placeholders.
    /// </summary>
    [ObservableProperty]
    private bool _includeAuthentication;

    /// <summary>The generated cURL command, updated whenever <see cref="IncludeAuthentication"/> changes.</summary>
    [ObservableProperty]
    private string _curlCommandText = string.Empty;

    public CurlDialogViewModel(RequestModel request, CurlAuthMaskInfo? authMaskInfo)
    {
        _request = request;
        _authMaskInfo = authMaskInfo;

        HasAuthentication =
            request.Headers.ContainsKey(WellKnownHeaders.Authorization) ||
            authMaskInfo is not null;

        // Build initial text — mask auth by default when auth is present.
        _curlCommandText = CurlCommandBuilder.Build(
            request,
            maskAuthentication: HasAuthentication,
            authMaskInfo);
    }

    partial void OnIncludeAuthenticationChanged(bool value)
    {
        CurlCommandText = CurlCommandBuilder.Build(
            _request,
            maskAuthentication: HasAuthentication && !value,
            _authMaskInfo);
    }
}
