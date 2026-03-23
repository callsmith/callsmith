namespace Callsmith.Core.Models;

/// <summary>
/// Threshold constants used to decide when to surface maintenance prompts in the
/// history UI. These are conservative defaults for Phase 7 and will be replaced by
/// user-configurable settings in Phase 11.
/// </summary>
public static class HistoryMaintenancePolicy
{
    /// <summary>
    /// If a history query takes longer than this many milliseconds, the UI displays an
    /// inline non-blocking suggestion to purge old entries.
    /// </summary>
    public const int LatencyPromptThresholdMs = 3_000;

    /// <summary>
    /// If the total number of history entries exceeds this count, the UI displays a
    /// non-blocking banner suggesting cleanup on the next history open or after a new
    /// entry is saved.
    /// </summary>
    public const int RecordCountPromptThreshold = 10_000;
}
