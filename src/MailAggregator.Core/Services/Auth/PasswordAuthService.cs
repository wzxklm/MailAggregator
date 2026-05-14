using MailAggregator.Core.Models;
using Serilog;

namespace MailAggregator.Core.Services.Auth;

public class PasswordAuthService : IPasswordAuthService
{
    private readonly ICredentialEncryptionService _encryptionService;
    private readonly ILogger _logger;

    public PasswordAuthService(ICredentialEncryptionService encryptionService, ILogger logger)
    {
        _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void StorePassword(Account account, string plainPassword)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (string.IsNullOrEmpty(plainPassword))
            throw new ArgumentException("Password cannot be null or empty.", nameof(plainPassword));

        account.EncryptedPassword = _encryptionService.Encrypt(plainPassword);
        account.AuthType = AuthType.Password;

        _logger.Information("Stored encrypted password for account {EmailAddress}", account.EmailAddress);
    }

    public string RetrievePassword(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!HasStoredPassword(account))
            throw new InvalidOperationException(
                $"No password stored for account '{account.EmailAddress}'.");

        var plainPassword = _encryptionService.Decrypt(account.EncryptedPassword!);

        _logger.Information("Retrieved password for account {EmailAddress}", account.EmailAddress);

        return plainPassword;
    }

    public bool HasStoredPassword(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return !string.IsNullOrEmpty(account.EncryptedPassword);
    }

    public void ClearPassword(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        account.EncryptedPassword = null;

        _logger.Information("Cleared password for account {EmailAddress}", account.EmailAddress);
    }
}
