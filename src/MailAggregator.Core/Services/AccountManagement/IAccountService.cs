using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.AccountManagement;

public interface IAccountService
{
    /// <summary>
    /// Adds a new email account by discovering server configuration,
    /// determining auth type (OAuth or password), validating the connection, and saving to the database.
    /// </summary>
    /// <param name="emailAddress">The email address to add.</param>
    /// <param name="password">The plain-text password (null for OAuth accounts).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The saved Account entity.</returns>
    Task<Account> AddAccountAsync(string emailAddress, string? password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing account's configuration (server settings, proxy, display name, etc.).
    /// </summary>
    Task<Account> UpdateAccountAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an account and all its local cached data (folders, messages, attachments, credentials).
    /// </summary>
    Task DeleteAccountAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all accounts.
    /// </summary>
    Task<IReadOnlyList<Account>> GetAllAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single account by ID, or null if not found.
    /// </summary>
    Task<Account?> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that we can connect to the account's IMAP server.
    /// </summary>
    Task<bool> ValidateConnectionAsync(Account account, CancellationToken cancellationToken = default);
}
