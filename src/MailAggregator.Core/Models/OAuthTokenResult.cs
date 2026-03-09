namespace MailAggregator.Core.Models;

public class OAuthTokenResult
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Scopes granted by the authorization server. May differ from requested scopes
    /// (e.g., Microsoft often omits offline_access from the response).
    /// </summary>
    public IReadOnlyList<string> GrantedScopes { get; set; } = Array.Empty<string>();
}
