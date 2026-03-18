using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Callsmith.Desktop.Messages;

/// <summary>
/// Sent when a request should be revealed and highlighted in the collections sidebar.
/// The value is the absolute file path of the request to reveal.
/// </summary>
public sealed class RevealRequestMessage(string filePath) : ValueChangedMessage<string>(filePath);
