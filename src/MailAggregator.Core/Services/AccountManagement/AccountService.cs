using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using MailAggregator.Core.Services.Discovery;
using MailAggregator.Core.Services.Mail;
using MailAggregator.Core.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MailAggregator.Core.Services.AccountManagement;

public class AccountService : IAccountService
{
    private readonly MailAggregatorDbContext _dbContext;
    private readonly IAutoDiscoveryService _autoDiscoveryService;
    private readonly IOAuthService _oAuthService;
    private readonly IPasswordAuthService _passwordAuthService;
    private readonly IImapConnectionService _imapConnectionService;
    private readonly ISyncManager _syncManager;
    private readonly IImapConnectionPool _connectionPool;
    private readonly ILogger _logger;

    public AccountService(
        MailAggregatorDbContext dbContext,
        IAutoDiscoveryService autoDiscoveryService,
        IOAuthService oAuthService,
        IPasswordAuthService passwordAuthService,
        IImapConnectionService imapConnectionService,
        ISyncManager syncManager,
        IImapConnectionPool connectionPool,
        ILogger logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _autoDiscoveryService = autoDiscoveryService ?? throw new ArgumentNullException(nameof(autoDiscoveryService));
        _oAuthService = oAuthService ?? throw new ArgumentNullException(nameof(oAuthService));
        _passwordAuthService = passwordAuthService ?? throw new ArgumentNullException(nameof(passwordAuthService));
        _imapConnectionService = imapConnectionService ?? throw new ArgumentNullException(nameof(imapConnectionService));
        _syncManager = syncManager ?? throw new ArgumentNullException(nameof(syncManager));
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Account> AddAccountAsync(string emailAddress, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
            throw new ArgumentException("Email address cannot be null or empty.", nameof(emailAddress));

        _logger.Information("Adding account for {EmailAddress}", emailAddress);

        // Step 0: Check for duplicate account
        var exists = await _dbContext.Accounts
            .AnyAsync(a => a.EmailAddress == emailAddress, cancellationToken);
        if (exists)
        {
            _logger.Warning("Duplicate account detected for {EmailAddress}", emailAddress);
            throw new InvalidOperationException(
                $"An account with email '{emailAddress}' already exists.");
        }

        // Step 1: Auto-discover server configuration
        var serverConfig = await _autoDiscoveryService.DiscoverAsync(emailAddress, cancellationToken);

        // Step 2: If discovery fails, throw
        if (serverConfig is null)
        {
            _logger.Error("Auto-discovery failed for {EmailAddress}", emailAddress);
            throw new InvalidOperationException(
                $"Could not discover server configuration for '{emailAddress}'. Please configure manually.");
        }

        _logger.Information("Discovered server config for {EmailAddress}: IMAP={ImapHost}:{ImapPort}, SMTP={SmtpHost}:{SmtpPort}",
            emailAddress, serverConfig.ImapHost, serverConfig.ImapPort, serverConfig.SmtpHost, serverConfig.SmtpPort);

        // Step 3: Create new Account from the discovered config
        var account = new Account
        {
            EmailAddress = emailAddress,
            ImapHost = serverConfig.ImapHost,
            ImapPort = serverConfig.ImapPort,
            ImapEncryption = serverConfig.ImapEncryption,
            SmtpHost = serverConfig.SmtpHost,
            SmtpPort = serverConfig.SmtpPort,
            SmtpEncryption = serverConfig.SmtpEncryption,
            IsEnabled = true
        };

        // Step 4: Determine auth type by checking for OAuth provider
        var oauthProvider = _oAuthService.FindProviderByHost(serverConfig.ImapHost);

        // Step 5/6: Configure authentication
        if (oauthProvider is not null)
        {
            // OAuth provider found - mark the auth type (OAuth flow is UI-driven)
            account.AuthType = AuthType.OAuth2;
            _logger.Information("OAuth provider '{ProviderName}' detected for {EmailAddress}",
                oauthProvider.Name, emailAddress);
        }
        else
        {
            // Password authentication
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException(
                    "Password is required for non-OAuth accounts.", nameof(password));

            _passwordAuthService.StorePassword(account, password);
            _logger.Information("Password authentication configured for {EmailAddress}", emailAddress);
        }

        // Step 7: Validate the connection (only for password auth; OAuth has no tokens yet)
        if (account.AuthType == AuthType.Password)
        {
            _logger.Information("Validating IMAP connection for {EmailAddress}", emailAddress);

            var isValid = await ValidateConnectionAsync(account, cancellationToken);
            if (!isValid)
            {
                _logger.Error("Connection validation failed for {EmailAddress}", emailAddress);
                throw new InvalidOperationException(
                    $"Could not connect to IMAP server for '{emailAddress}'. Please verify your credentials and server settings.");
            }
        }

        // Step 8: Save to database
        _dbContext.Accounts.Add(account);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.Information("Account added successfully for {EmailAddress} with ID {AccountId}",
            emailAddress, account.Id);

        return account;
    }

    public async Task<Account> UpdateAccountAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        _logger.Information("Updating account {AccountId} ({EmailAddress})", account.Id, account.EmailAddress);

        _dbContext.Accounts.Update(account);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.Information("Account {AccountId} updated successfully", account.Id);

        return account;
    }

    public async Task DeleteAccountAsync(int accountId, CancellationToken cancellationToken = default)
    {
        _logger.Information("Deleting account {AccountId}", accountId);

        var account = await _dbContext.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

        if (account is null)
        {
            _logger.Error("Account {AccountId} not found for deletion", accountId);
            throw new InvalidOperationException($"Account with ID {accountId} not found.");
        }

        // Step 1: Stop background sync for this account
        await _syncManager.StopAccountSyncAsync(accountId);

        // Step 2: Release pooled IMAP connections
        _connectionPool.RemoveAccount(accountId);

        // Step 3: Clean up downloaded attachment files from disk
        var attachmentPaths = await _dbContext.Messages
            .Where(m => m.AccountId == accountId)
            .SelectMany(m => m.Attachments)
            .Where(a => a.LocalPath != null)
            .Select(a => a.LocalPath!)
            .ToListAsync(cancellationToken);

        foreach (var path in attachmentPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.Debug("Deleted attachment file {Path}", path);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to delete attachment file {Path}", path);
            }
        }

        // Step 4: Remove the account (cascade delete handles DB entities)
        _dbContext.Accounts.Remove(account);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.Information("Account {AccountId} ({EmailAddress}) deleted successfully with resource cleanup",
            accountId, account.EmailAddress);
    }

    public async Task<IReadOnlyList<Account>> GetAllAccountsAsync(CancellationToken cancellationToken = default)
    {
        _logger.Debug("Retrieving all accounts");

        var accounts = await _dbContext.Accounts
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        _logger.Debug("Retrieved {Count} accounts", accounts.Count);

        return accounts.AsReadOnly();
    }

    public async Task<Account?> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        _logger.Debug("Retrieving account {AccountId}", accountId);

        var account = await _dbContext.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

        if (account is null)
        {
            _logger.Warning("Account {AccountId} not found", accountId);
        }

        return account;
    }

    public async Task<bool> ValidateConnectionAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        _logger.Information("Validating connection for account {EmailAddress}", account.EmailAddress);

        try
        {
            using var client = await _imapConnectionService.ConnectAsync(account, cancellationToken);

            _logger.Information("Connection validation succeeded for {EmailAddress}", account.EmailAddress);

            if (client.IsConnected)
                await client.DisconnectAsync(true, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Connection validation failed for {EmailAddress}", account.EmailAddress);
            return false;
        }
    }
}
