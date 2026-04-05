namespace Callsmith.Core.Models;

/// <summary>
/// Authentication configuration attached to a saved request.
/// Only the fields relevant to the chosen <see cref="AuthType"/> are expected to be populated.
/// </summary>
public sealed class AuthConfig
{
    /// <summary>The authentication strategy to apply when sending this request.</summary>
    public string AuthType { get; init; } = AuthTypes.Inherit;

    /// <summary>Bearer token. Used when <see cref="AuthType"/> is <see cref="AuthTypes.Bearer"/>.</summary>
    public string? Token { get; init; }

    /// <summary>Basic auth username. Used when <see cref="AuthType"/> is <see cref="AuthTypes.Basic"/>.</summary>
    public string? Username { get; init; }

    /// <summary>Basic auth password. Used when <see cref="AuthType"/> is <see cref="AuthTypes.Basic"/>.</summary>
    public string? Password { get; init; }

    /// <summary>API key header/query param name. Used when <see cref="AuthType"/> is <see cref="AuthTypes.ApiKey"/>.</summary>
    public string? ApiKeyName { get; init; }

    /// <summary>API key value. Used when <see cref="AuthType"/> is <see cref="AuthTypes.ApiKey"/>.</summary>
    public string? ApiKeyValue { get; init; }

    /// <summary>Whether to add the API key as a header or a query parameter.</summary>
    public string ApiKeyIn { get; init; } = ApiKeyLocations.Header;

    /// <summary>Well-known auth type constants.</summary>
    public static class AuthTypes
    {
        /// <summary>
        /// Inherit the auth configuration from the parent folder.
        /// This is the default for both requests and folders.
        /// If the root folder is also set to inherit, effective auth is <see cref="None"/>.
        /// </summary>
        public const string Inherit = "inherit";

        /// <summary>No authentication credentials are sent.</summary>
        public const string None = "none";
        public const string Bearer = "bearer";
        public const string Basic = "basic";
        public const string ApiKey = "apikey";
    }

    /// <summary>Well-known locations for API key injection.</summary>
    public static class ApiKeyLocations
    {
        public const string Header = "header";
        public const string Query = "query";
    }
}
