namespace MailAggregator.Core.Services.Auth;

public interface ICredentialEncryptionService
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}
