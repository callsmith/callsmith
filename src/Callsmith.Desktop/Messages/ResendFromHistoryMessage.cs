using Callsmith.Core.Models;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Sent when the user clicks "Resend Request" in the history panel.
/// The request editor opens a new unsaved tab pre-populated with the fully-resolved values.
/// Sensitive fields should already be revealed before this message is sent.
/// </summary>
public sealed record ResendFromHistoryMessage(HistoryEntry Entry);
