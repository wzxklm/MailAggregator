using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MailAggregator.Core.Services.Mail;

public class ImapConnectionService : IImapConnectionService
{
    private readonly ICredentialEncryptionService _encryption;
    private readonly IOAuthService _oAuthService;
    private readonly IDbContextFactory<MailAggregatorDbContext> _dbContextFactory;
    private readonly ILogger _logger;

    public ImapConnectionService(
        ICredentialEncryptionService encryption,
        IOAuthService oAuthService,
        IDbContextFactory<MailAggregatorDbContext> dbContextFactory,
        ILogger logger)
    {
        _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));
        _oAuthService = oAuthService ?? throw new ArgumentNullException(nameof(oAuthService));
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ImapClient> ConnectAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        Exception? lastException = null;

        for (int attempt = 0; attempt < MailConnectionHelper.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = MailConnectionHelper.InitialRetryDelay * Math.Pow(2, attempt - 1);
                _logger.Warning("IMAP connection attempt {Attempt}/{MaxRetries} for {Email}, retrying in {Delay}s",
                    attempt + 1, MailConnectionHelper.MaxRetries, account.EmailAddress, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }

            var client = new ImapClient();
            try
            {
                MailConnectionHelper.ConfigureProxy(client, account, "IMAP", _logger);

                var secureSocketOptions = MailConnectionHelper.GetSecureSocketOptions(account.ImapEncryption);
                _logger.Information("Connecting IMAP to {Host}:{Port} ({Encryption}) for {Email}",
                    account.ImapHost, account.ImapPort, account.ImapEncryption, account.EmailAddress);

                await client.ConnectAsync(account.ImapHost, account.ImapPort, secureSocketOptions, cancellationToken);

                // Send IMAP ID command before authentication (RFC 2971).
                // Providers like 163.com (Coremail) require the client to identify itself
                // before accepting LOGIN, otherwise they reject with "Unsafe Login".
                if (client.Capabilities.HasFlag(ImapCapabilities.Id))
                {
                    try
                    {
                        await client.IdentifyAsync(new ImapImplementation
                        {
                            Name = "MailAggregator",
                            Version = "1.0"
                        }, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "IMAP ID command failed for {Email}, continuing without identification",
                            account.EmailAddress);
                    }
                }

                await MailConnectionHelper.AuthenticateAsync(client, account, _encryption, cancellationToken, _oAuthService,
                    onTokenRefreshed: PersistRefreshedTokenAsync);

                _logger.Information("IMAP connected and authenticated for {Email}", account.EmailAddress);
                return client;
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                client.Dispose();
                lastException = ex;
                _logger.Error(ex, "IMAP connection attempt {Attempt}/{MaxRetries} failed for {Email}",
                    attempt + 1, MailConnectionHelper.MaxRetries, account.EmailAddress);
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to IMAP server {account.ImapHost}:{account.ImapPort} after {MailConnectionHelper.MaxRetries} attempts.",
            lastException);
    }

    private async Task PersistRefreshedTokenAsync(Account account, CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.Information("Persisted refreshed OAuth tokens for {Email}", account.EmailAddress);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to persist refreshed OAuth tokens for {Email}", account.EmailAddress);
        }
    }
}
