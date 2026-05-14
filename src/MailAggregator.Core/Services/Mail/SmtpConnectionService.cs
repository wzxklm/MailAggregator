using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MailAggregator.Core.Services.Mail;

public class SmtpConnectionService : ISmtpConnectionService
{
    private readonly ICredentialEncryptionService _encryption;
    private readonly IOAuthService _oAuthService;
    private readonly IDbContextFactory<MailAggregatorDbContext> _dbContextFactory;
    private readonly ILogger _logger;

    public SmtpConnectionService(
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

    public async Task<SmtpClient> ConnectAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        Exception? lastException = null;

        for (int attempt = 0; attempt < MailConnectionHelper.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = MailConnectionHelper.InitialRetryDelay * Math.Pow(2, attempt - 1);
                _logger.Warning("SMTP connection attempt {Attempt}/{MaxRetries} for {Email}, retrying in {Delay}s",
                    attempt + 1, MailConnectionHelper.MaxRetries, account.EmailAddress, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }

            var client = new SmtpClient();
            try
            {
                MailConnectionHelper.ConfigureProxy(client, account, "SMTP", _logger);

                var secureSocketOptions = MailConnectionHelper.GetSecureSocketOptions(account.SmtpEncryption);
                _logger.Information("Connecting SMTP to {Host}:{Port} ({Encryption}) for {Email}",
                    account.SmtpHost, account.SmtpPort, account.SmtpEncryption, account.EmailAddress);

                await client.ConnectAsync(account.SmtpHost, account.SmtpPort, secureSocketOptions, cancellationToken);
                await MailConnectionHelper.AuthenticateAsync(client, account, _encryption, cancellationToken, _oAuthService,
                    onTokenRefreshed: PersistRefreshedTokenAsync);

                _logger.Information("SMTP connected and authenticated for {Email}", account.EmailAddress);
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
                _logger.Error(ex, "SMTP connection attempt {Attempt}/{MaxRetries} failed for {Email}",
                    attempt + 1, MailConnectionHelper.MaxRetries, account.EmailAddress);
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to SMTP server {account.SmtpHost}:{account.SmtpPort} after {MailConnectionHelper.MaxRetries} attempts.",
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
