using Callsmith.Core.Abstractions;
using Callsmith.Core.Models;

namespace Callsmith.Core;

/// <summary>
/// Resolves the correct <see cref="ITransport"/> implementation at runtime based
/// on the URI scheme of the request URL. Transports are registered at startup via
/// <see cref="Register"/>.
/// </summary>
public sealed class TransportRegistry
{
    private readonly Dictionary<string, ITransport> _transports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a transport for all URI schemes it declares in
    /// <see cref="ITransport.SupportedSchemes"/>.
    /// </summary>
    /// <param name="transport">The transport to register.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is null.</exception>
    public void Register(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        foreach (var scheme in transport.SupportedSchemes)
            _transports[scheme] = transport;
    }

    /// <summary>
    /// Returns the transport registered for the URI scheme of <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The request whose URL scheme is used for lookup.</param>
    /// <returns>The matching <see cref="ITransport"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no transport is registered for the request's URI scheme.
    /// </exception>
    public ITransport Resolve(RequestModel request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Request URL '{request.Url}' is not a valid absolute URI.");

        if (_transports.TryGetValue(uri.Scheme, out var transport))
            return transport;

        throw new InvalidOperationException(
            $"No transport is registered for URI scheme '{uri.Scheme}'. " +
            $"Registered schemes: {string.Join(", ", _transports.Keys)}");
    }
}
