namespace MailAggregator.Core.Services.Auth;

public interface IPasswordAuthService
{
    /// <summary>
    /// Encrypts and stores a password for the given account.
    /// Updates the account's EncryptedPassword and AuthType fields.
    /// </summary>
    void StorePassword(Models.Account account, string plainPassword);

    /// <summary>
    /// Retrieves the decrypted password for the given account.
    /// Throws InvalidOperationException if no password is stored.
    /// </summary>
    string RetrievePassword(Models.Account account);

    /// <summary>
    /// Returns true if the account has a stored password.
    /// </summary>
    bool HasStoredPassword(Models.Account account);

    /// <summary>
    /// Clears the stored password from the account.
    /// </summary>
    void ClearPassword(Models.Account account);
}
