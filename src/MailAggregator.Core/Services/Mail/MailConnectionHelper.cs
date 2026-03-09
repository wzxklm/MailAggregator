using System.Collections.Concurrent;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using MailKit;
using MailKit.Net.Proxy;
using MailKit.Security;
using Serilog;

namespace MailAggregator.Core.Services.Mail;

internal static class MailConnectionHelper
{
    internal const int MaxRetries = 3;
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Grace period for token refresh: refresh tokens 60 seconds before actual expiry
    /// to avoid authentication failures due to clock skew or network latency.
    /// </summary>
    internal static readonly TimeSpan TokenRefreshGracePeriod = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Per-account semaphores to prevent concurrent token refreshes.
    /// When IMAP and SMTP connections refresh the same account's token simultaneously,
    /// providers like Google invalidate the old refresh token, causing one refresh to fail.
    /// </summary>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _tokenRefreshLocks = new();

    /// <summary>
    /// Removes the token refresh lock for a deleted account to prevent memory leaks.
    /// Called by AccountService.DeleteAccountAsync during account cleanup.
    /// </summary>
    internal static void RemoveTokenRefreshLock(int accountId)
    {
        if (_tokenRefreshLocks.TryRemove(accountId, out var semaphore))
            semaphore.Dispose();
    }

    internal static SecureSocketOptions GetSecureSocketOptions(ConnectionEncryptionType encryption) => encryption switch
    {
        ConnectionEncryptionType.Ssl => SecureSocketOptions.SslOnConnect,
        ConnectionEncryptionType.StartTls => SecureSocketOptions.StartTls,
        ConnectionEncryptionType.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto
    };

    internal static void ConfigureProxy(MailService client, Account account, string protocol, ILogger logger)
    {
        if (!string.IsNullOrEmpty(account.ProxyHost) && account.ProxyPort.HasValue)
        {
            client.ProxyClient = new Socks5Client(account.ProxyHost, account.ProxyPort.Value);
            logger.Information("{Protocol} using SOCKS5 proxy {ProxyHost}:{ProxyPort} for {Email}",
                protocol, account.ProxyHost, account.ProxyPort.Value, account.EmailAddress);
        }
    }

    internal static async Task AuthenticateAsync(
        MailService client,
        Account account,
        ICredentialEncryptionService encryption,
        CancellationToken cancellationToken,
        IOAuthService? oAuthService = null,
        Func<Account, CancellationToken, Task>? onTokenRefreshed = null)
    {
        if (account.AuthType == AuthType.OAuth2)
        {
            if (string.IsNullOrEmpty(account.EncryptedAccessToken))
                throw new InvalidOperationException("OAuth2 account is missing access token.");

            // Refresh token if expired or about to expire (with grace period).
            // Use per-account lock to prevent IMAP and SMTP from refreshing concurrently,
            // which can cause providers like Google to invalidate the old refresh token.
            if (oAuthService != null
                && account.OAuthTokenExpiry.HasValue
                && account.OAuthTokenExpiry.Value <= DateTimeOffset.UtcNow.Add(TokenRefreshGracePeriod)
                && !string.IsNullOrEmpty(account.EncryptedRefreshToken))
            {
                var refreshLock = _tokenRefreshLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
                await refreshLock.WaitAsync(cancellationToken);
                try
                {
                    // Re-check expiry after acquiring lock — another caller may have already refreshed
                    if (account.OAuthTokenExpiry.Value <= DateTimeOffset.UtcNow.Add(TokenRefreshGracePeriod))
                    {
                        var provider = oAuthService.FindProviderByHost(account.ImapHost);
                        if (provider != null)
                        {
                            var refreshed = await oAuthService.RefreshTokenAsync(provider, account.EncryptedRefreshToken, cancellationToken);
                            account.EncryptedAccessToken = refreshed.AccessToken;
                            if (refreshed.RefreshToken != null)
                                account.EncryptedRefreshToken = refreshed.RefreshToken;
                            account.OAuthTokenExpiry = refreshed.ExpiresAt;

                            // Persist the refreshed tokens to the database
                            if (onTokenRefreshed != null)
                                await onTokenRefreshed(account, cancellationToken);
                        }
                    }
                }
                finally
                {
                    refreshLock.Release();
                }
            }

            var accessToken = encryption.Decrypt(account.EncryptedAccessToken);
            var oauth2 = new SaslMechanismOAuth2(account.EmailAddress, accessToken);
            await client.AuthenticateAsync(oauth2, cancellationToken);
        }
        else
        {
            if (string.IsNullOrEmpty(account.EncryptedPassword))
                throw new InvalidOperationException("Password account is missing password.");

            var password = encryption.Decrypt(account.EncryptedPassword);
            await client.AuthenticateAsync(account.EmailAddress, password, cancellationToken);
        }
    }
}
