namespace MailAggregator.Core.Services.Auth;

/// <summary>
/// Thrown when an OAuth token refresh fails with "invalid_grant", indicating
/// the refresh token has been revoked or expired. The user must re-authenticate
/// via the full OAuth authorization flow.
/// </summary>
public class OAuthReauthenticationRequiredException : Exception
{
    public OAuthReauthenticationRequiredException(string message)
        : base(message) { }

    public OAuthReauthenticationRequiredException(string message, Exception innerException)
        : base(message, innerException) { }
}
