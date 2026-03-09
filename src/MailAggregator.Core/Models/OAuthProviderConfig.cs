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
    /// Whether this provider requires PKCE (Proof Key for Code Exchange).
    /// When false, code_challenge/code_challenge_method are omitted from authorization requests
    /// and code_verifier is omitted from token exchange requests.
    /// Defaults to true. Google/Yahoo/AOL do not use PKCE; Microsoft/Fastmail do.
    /// </summary>
    public bool UsePKCE { get; set; } = true;

    /// <summary>
    /// Optional custom redirection endpoint required by some providers (e.g. Yahoo/AOL use http://localhost).
    /// When set, this scheme and host are used as the redirect_uri base in authorization requests.
    /// </summary>
    public string? RedirectionEndpoint { get; set; }

    /// <summary>
    /// Additional query parameters to include in the authorization URL (e.g. access_type=offline for Google).
    /// </summary>
    public Dictionary<string, string> AdditionalAuthParams { get; set; } = new();
}
