using FluentAssertions;
using MailAggregator.Core.Services.Auth;

namespace MailAggregator.Tests.Services.Auth;

public class CredentialEncryptionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CredentialEncryptionService _service;

    public CredentialEncryptionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mail_agg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var keyPath = Path.Combine(_tempDir, "test.key");
        _service = new CredentialEncryptionService(new DevKeyProtector(), keyPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_ReturnsOriginal()
    {
        var plaintext = "my-secret-password-123!@#";

        var encrypted = _service.Encrypt(plaintext);
        var decrypted = _service.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var plaintext = "same-password";

        var encrypted1 = _service.Encrypt(plaintext);
        var encrypted2 = _service.Encrypt(plaintext);

        encrypted1.Should().NotBe(encrypted2, "each encryption uses a random nonce");
    }

    [Fact]
    public void Decrypt_WithTamperedData_ThrowsCryptographicException()
    {
        var encrypted = _service.Encrypt("test-data");
        var data = Convert.FromBase64String(encrypted);
        data[15] ^= 0xFF; // tamper with ciphertext
        var tampered = Convert.ToBase64String(data);

        var act = () => _service.Decrypt(tampered);

        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void Encrypt_EmptyString_ThrowsArgumentException()
    {
        var act = () => _service.Encrypt("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decrypt_EmptyString_ThrowsArgumentException()
    {
        var act = () => _service.Decrypt("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encrypt_Decrypt_UnicodeContent_RoundTrips()
    {
        var plaintext = "密码测试 🔐 Пароль";

        var encrypted = _service.Encrypt(plaintext);
        var decrypted = _service.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_Decrypt_LongContent_RoundTrips()
    {
        var plaintext = new string('A', 10000);

        var encrypted = _service.Encrypt(plaintext);
        var decrypted = _service.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void KeyPersistence_NewServiceInstance_CanDecryptPreviousData()
    {
        var keyPath = Path.Combine(_tempDir, "persist.key");
        var protector = new DevKeyProtector();

        var service1 = new CredentialEncryptionService(protector, keyPath);
        var encrypted = service1.Encrypt("persistent-secret");

        var service2 = new CredentialEncryptionService(protector, keyPath);
        var decrypted = service2.Decrypt(encrypted);

        decrypted.Should().Be("persistent-secret");
    }
}
