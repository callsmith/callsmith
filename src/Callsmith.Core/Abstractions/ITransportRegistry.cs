using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Resolves the correct <see cref="ITransport"/> implementation at runtime
/// based on the URI scheme of the request URL.
/// </summary>
public interface ITransportRegistry
{
    /// <summary>
    /// Returns the transport registered for the URI scheme of <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The request whose URL scheme is used for lookup.</param>
    /// <returns>The matching <see cref="ITransport"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no transport is registered for the request's URI scheme.
    /// </exception>
    ITransport Resolve(RequestModel request);
}
