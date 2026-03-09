namespace MailAggregator.Core.Models;

/// <summary>
/// Configuration for an OAuth 2.0 provider, loaded from oauth-providers.json.
/// </summary>
public class OAuthProviderConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// IMAP/SMTP server hostnames that this provider handles.
    /// Used to match against discovered server configuration.
    /// </summary>
    public List<string> ServerHosts { get; set; } = new();

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Optional custom redirection endpoint required by some providers (e.g. Yahoo/AOL use https://127.0.0.1).
    /// When set, this is used as the redirect_uri base in authorization requests.
    /// </summary>
    public string? RedirectionEndpoint { get; set; }

    /// <summary>
    /// Additional query parameters to include in the authorization URL (e.g. access_type=offline for Google).
    /// </summary>
    public Dictionary<string, string> AdditionalAuthParams { get; set; } = new();
}
