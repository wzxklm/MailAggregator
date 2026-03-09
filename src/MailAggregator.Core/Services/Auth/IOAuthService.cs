namespace MailAggregator.Core.Services.Auth;

using MailAggregator.Core.Models;

public interface IOAuthService
{
    /// <summary>
    /// Finds the OAuth provider config that matches the given IMAP/SMTP server host.
    /// Returns null if no OAuth provider is configured for this server.
    /// </summary>
    OAuthProviderConfig? FindProviderByHost(string serverHost);

    /// <summary>
    /// Generates the authorization URL for the OAuth PKCE flow.
    /// Returns the URL and the code_verifier (needed later for token exchange).
    /// </summary>
    (string authorizationUrl, string codeVerifier, int listenerPort) PrepareAuthorization(OAuthProviderConfig provider, string? loginHint = null);

    /// <summary>
    /// Starts a local HTTP listener on the specified port and waits for the OAuth callback.
    /// Returns the authorization code from the callback.
    /// </summary>
    Task<string> WaitForAuthorizationCodeAsync(int listenerPort, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges the authorization code for access and refresh tokens.
    /// Tokens are encrypted before being returned.
    /// </summary>
    Task<OAuthTokenResult> ExchangeCodeForTokenAsync(OAuthProviderConfig provider, string authorizationCode, string codeVerifier, string redirectUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes an expired access token using the refresh token.
    /// </summary>
    Task<OAuthTokenResult> RefreshTokenAsync(OAuthProviderConfig provider, string encryptedRefreshToken, CancellationToken cancellationToken = default);
}
