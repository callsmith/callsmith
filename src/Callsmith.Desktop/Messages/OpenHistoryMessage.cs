namespace Callsmith.Desktop.Messages;

/// <summary>
/// Requests opening the history panel.
/// When <see cref="RequestId"/> is set, history should be scoped to that request.
/// </summary>
public sealed record OpenHistoryMessage(Guid? RequestId = null, string? RequestName = null);
