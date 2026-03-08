using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace MailAggregator.Core.Services.Auth;

/// <summary>
/// AES-256-GCM encryption for credentials (passwords, OAuth tokens).
/// Storage format: Base64(nonce[12] + ciphertext[N] + tag[16])
/// The AES key is generated once and persisted via IKeyProtector.
/// </summary>
public class CredentialEncryptionService : ICredentialEncryptionService
{
    private const int NonceSize = 12; // AES-GCM standard nonce size
    private const int TagSize = 16;   // AES-GCM tag size
    private const int KeySize = 32;   // AES-256

    private readonly byte[] _key;

    public CredentialEncryptionService(IKeyProtector keyProtector, string keyFilePath)
    {
        _key = LoadOrCreateKey(keyProtector, keyFilePath);
    }

    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            throw new ArgumentException("Plaintext cannot be null or empty.", nameof(plaintext));

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(_key, TagSize);
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            // Pack: nonce + ciphertext + tag
            var result = new byte[NonceSize + ciphertext.Length + TagSize];
            nonce.CopyTo(result.AsSpan(0, NonceSize));
            ciphertext.CopyTo(result.AsSpan(NonceSize, ciphertext.Length));
            tag.CopyTo(result.AsSpan(NonceSize + ciphertext.Length, TagSize));

            return Convert.ToBase64String(result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public string Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64))
            throw new ArgumentException("Encrypted text cannot be null or empty.", nameof(encryptedBase64));

        var data = Convert.FromBase64String(encryptedBase64);

        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted data is too short.");

        var ciphertextLength = data.Length - NonceSize - TagSize;
        var nonce = data.AsSpan(0, NonceSize);
        var ciphertext = data.AsSpan(NonceSize, ciphertextLength);
        var tag = data.AsSpan(NonceSize + ciphertextLength, TagSize);

        var plaintext = new byte[ciphertextLength];
        try
        {
            using var aesGcm = new AesGcm(_key, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] LoadOrCreateKey(IKeyProtector keyProtector, string keyFilePath)
    {
        var directory = Path.GetDirectoryName(keyFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Atomic create to avoid TOCTOU race: two processes generating different keys
        try
        {
            var key = new byte[KeySize];
            RandomNumberGenerator.Fill(key);
            var protectedBytes = keyProtector.Protect(key);

            using var fs = new FileStream(keyFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(protectedBytes);

            Log.Information("Generated new encryption key at {KeyFilePath}", keyFilePath);
            return key;
        }
        catch (IOException) when (File.Exists(keyFilePath))
        {
            // File was created by another process — read it
        }

        Log.Debug("Loading encryption key from {KeyFilePath}", keyFilePath);
        var protectedKey = File.ReadAllBytes(keyFilePath);
        return keyProtector.Unprotect(protectedKey);
    }
}
