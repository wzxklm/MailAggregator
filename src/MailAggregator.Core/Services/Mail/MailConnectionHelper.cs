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
        CancellationToken cancellationToken)
    {
        if (account.AuthType == AuthType.OAuth2)
        {
            if (string.IsNullOrEmpty(account.EncryptedAccessToken))
                throw new InvalidOperationException("OAuth2 account is missing access token.");

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
