namespace Callsmith.Core.Import;

/// <summary>
/// Options that control the behaviour of a collection import operation.
/// </summary>
public sealed record CollectionImportOptions
{
    /// <summary>
    /// Default options: <see cref="ImportMergeStrategy.Skip"/> merge strategy and
    /// the standard <c>baseUrl</c> variable name.
    /// </summary>
    public static readonly CollectionImportOptions Default = new();

    /// <summary>
    /// Determines what happens when an imported request has the same name as a request
    /// already present in the target folder.
    /// Defaults to <see cref="ImportMergeStrategy.Skip"/>.
    /// Only applies when merging into an existing collection; ignored for new-collection imports.
    /// </summary>
    public ImportMergeStrategy MergeStrategy { get; init; } = ImportMergeStrategy.Skip;

    /// <summary>
    /// The environment-variable name used to hold the server base URL in OpenAPI / Swagger
    /// imports. Request URLs are generated as <c>{{BaseUrlVariableName}}/path</c>.
    /// Defaults to <c>baseUrl</c>.
    /// Only affects OpenAPI and Swagger imports; other formats use absolute URLs and ignore
    /// this setting.
    /// </summary>
    public string BaseUrlVariableName { get; init; } = "baseUrl";
}
