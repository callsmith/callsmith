using Callsmith.Core.Models;

namespace Callsmith.Core.Abstractions;

/// <summary>
/// Core network abstraction. Sends a <see cref="RequestModel"/> and returns a
/// <see cref="ResponseModel"/>. One implementation exists per protocol
/// (HTTP, WebSocket, gRPC, etc.).
/// </summary>
public interface ITransport
{
    /// <summary>
    /// The URI scheme this transport handles (e.g. "http", "https", "ws", "grpc").
    /// Used by <see cref="TransportRegistry"/> to route requests to the correct transport.
    /// </summary>
    IReadOnlyList<string> SupportedSchemes { get; }

    /// <summary>
    /// Sends the request and returns the full response, including headers, body,
    /// status code, and elapsed time.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct = default);
}
