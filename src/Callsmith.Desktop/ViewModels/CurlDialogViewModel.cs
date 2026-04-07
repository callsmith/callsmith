using Callsmith.Core.Helpers;
using Callsmith.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Callsmith.Desktop.ViewModels;

/// <summary>
/// View model for the cURL command dialog.
/// Manages the auth/secrets-masking toggle and regenerates the command text on change.
/// </summary>
public sealed partial class CurlDialogViewModel : ObservableObject
{
    private readonly RequestModel _request;
    private readonly CurlAuthMaskInfo? _authMaskInfo;
    private readonly IReadOnlySet<string> _secretValues;

    /// <summary>
    /// True when the request contains detectable authentication or secret environment
    /// variables that can be masked. Drives the IsEnabled state of the checkbox.
    /// </summary>
    public bool HasAuthentication { get; }

    /// <summary>
    /// When true, real credentials and secret values are shown in the cURL output.
    /// When false (default), they are replaced with placeholders.
    /// </summary>
    [ObservableProperty]
    private bool _includeAuthentication;

    /// <summary>The generated cURL command, updated whenever <see cref="IncludeAuthentication"/> changes.</summary>
    [ObservableProperty]
    private string _curlCommandText = string.Empty;

    public CurlDialogViewModel(
        RequestModel request,
        CurlAuthMaskInfo? authMaskInfo,
        IReadOnlySet<string>? secretValues = null)
    {
        _request = request;
        _authMaskInfo = authMaskInfo;
        _secretValues = secretValues ?? new HashSet<string>();

        HasAuthentication =
            request.Headers.ContainsKey(WellKnownHeaders.Authorization) ||
            authMaskInfo is not null ||
            _secretValues.Count > 0;

        // Build initial text — mask auth/secrets by default when any masking is applicable.
        _curlCommandText = CurlCommandBuilder.Build(
            request,
            maskAuthentication: HasAuthentication,
            authMaskInfo,
            _secretValues);
    }

    partial void OnIncludeAuthenticationChanged(bool value)
    {
        CurlCommandText = CurlCommandBuilder.Build(
            _request,
            maskAuthentication: HasAuthentication && !value,
            _authMaskInfo,
            _secretValues);
    }
}
