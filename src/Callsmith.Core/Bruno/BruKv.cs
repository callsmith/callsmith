namespace Callsmith.Core.Bruno;

/// <summary>
/// A single key-value entry within a <c>.bru</c> block, carrying an optional disabled flag.
/// </summary>
internal sealed class BruKv
{
    /// <summary>The key name, without any leading <c>~</c> disabled prefix.</summary>
    public string Key { get; }

    /// <summary>The raw value string (everything after the first <c>": "</c> on the line).</summary>
    public string Value { get; }

    /// <summary>
    /// <c>false</c> when the original line carried a <c>~</c> prefix, marking the entry as
    /// disabled in Bruno. Disabled items are preserved in the file for round-trip fidelity
    /// but are never sent with the HTTP request.
    /// </summary>
    public bool IsEnabled { get; }

    public BruKv(string key, string value, bool isEnabled = true)
    {
        Key = key;
        Value = value;
        IsEnabled = isEnabled;
    }
}
