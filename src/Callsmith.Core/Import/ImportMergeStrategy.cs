namespace Callsmith.Core.Import;

/// <summary>
/// Controls what happens when an imported request has the same name as one already
/// present in the target folder during a merge-into-collection import.
/// </summary>
public enum ImportMergeStrategy
{
    /// <summary>
    /// If a request with the same name already exists, skip it — leaving the existing
    /// request unchanged and not importing the duplicate.
    /// This is the default strategy.
    /// </summary>
    Skip,

    /// <summary>
    /// If a request with the same name already exists, keep the existing request and
    /// add the new one under a counter-suffixed name (e.g. "Get Users (1)").
    /// </summary>
    TakeBoth,

    /// <summary>
    /// If a request with the same name already exists, overwrite it with the imported one.
    /// The existing request is permanently replaced.
    /// </summary>
    Replace,
}
