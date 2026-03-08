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
}
