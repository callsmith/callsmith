namespace Callsmith.Core.Models;

/// <summary>
/// Records a single variable substitution that occurred during request preparation.
/// The token is the raw <c>{{name}}</c> placeholder; <see cref="ResolvedValue"/> is the
/// value it expanded to at send time.
/// </summary>
/// <param name="Token">The exact token string as it appeared in the request, e.g. <c>{{baseUrl}}</c>.</param>
/// <param name="ResolvedValue">
/// The value the token resolved to. For secret tokens (e.g. bearer tokens, API keys) the
/// raw value is stored here; the repository is responsible for encrypting it at rest.
/// </param>
/// <param name="IsSecret">
/// <see langword="true"/> when the resolved value came from a secret environment variable
/// (e.g. a Bearer token or API key value). The repository must encrypt this value before
/// persisting and decrypt it on demand via <see cref="Abstractions.IHistoryService.RevealSensitiveFieldsAsync"/>.
/// </param>
/// <param name="CiphertextValue">
/// When an entry is loaded from the repository and <see cref="IsSecret"/> is
/// <see langword="true"/>, this holds the raw ciphertext so that
/// <see cref="Abstractions.IHistoryService.RevealSensitiveFieldsAsync"/> can decrypt
/// in memory without an additional database round-trip.
/// <see langword="null"/> for non-secret bindings and for freshly-captured bindings
/// that have not yet been persisted.
/// </param>
public sealed record VariableBinding(
	string Token,
	string ResolvedValue,
	bool IsSecret,
	string? CiphertextValue = null);
