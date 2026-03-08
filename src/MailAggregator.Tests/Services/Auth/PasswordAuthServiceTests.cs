using FluentAssertions;
using Moq;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using Serilog;

namespace MailAggregator.Tests.Services.Auth;

public class PasswordAuthServiceTests
{
    private readonly Mock<ICredentialEncryptionService> _mockEncryption;
    private readonly PasswordAuthService _service;

    public PasswordAuthServiceTests()
    {
        _mockEncryption = new Mock<ICredentialEncryptionService>();
        _mockEncryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns<string>(s => $"ENC:{s}");
        _mockEncryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns<string>(s => s.Replace("ENC:", ""));

        var logger = new Mock<ILogger>();
        _service = new PasswordAuthService(_mockEncryption.Object, logger.Object);
    }

    [Fact]
    public void StorePassword_EncryptsAndSetsAccountFields()
    {
        var account = new Account { EmailAddress = "test@example.com" };

        _service.StorePassword(account, "my-secret");

        account.EncryptedPassword.Should().Be("ENC:my-secret");
        _mockEncryption.Verify(x => x.Encrypt("my-secret"), Times.Once);
    }

    [Fact]
    public void StorePassword_SetsAuthTypeToPassword()
    {
        var account = new Account { EmailAddress = "test@example.com", AuthType = AuthType.OAuth2 };

        _service.StorePassword(account, "password123");

        account.AuthType.Should().Be(AuthType.Password);
    }

    [Fact]
    public void RetrievePassword_ReturnsDecryptedPassword()
    {
        var account = new Account
        {
            EmailAddress = "test@example.com",
            EncryptedPassword = "ENC:my-secret"
        };

        var result = _service.RetrievePassword(account);

        result.Should().Be("my-secret");
        _mockEncryption.Verify(x => x.Decrypt("ENC:my-secret"), Times.Once);
    }

    [Fact]
    public void RetrievePassword_ThrowsInvalidOperationException_WhenNoPasswordStored()
    {
        var account = new Account { EmailAddress = "test@example.com", EncryptedPassword = null };

        var act = () => _service.RetrievePassword(account);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No password stored*");
    }

    [Fact]
    public void RetrievePassword_ThrowsInvalidOperationException_WhenPasswordIsEmpty()
    {
        var account = new Account { EmailAddress = "test@example.com", EncryptedPassword = "" };

        var act = () => _service.RetrievePassword(account);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void HasStoredPassword_ReturnsTrue_WhenPasswordExists()
    {
        var account = new Account { EncryptedPassword = "ENC:something" };

        _service.HasStoredPassword(account).Should().BeTrue();
    }

    [Fact]
    public void HasStoredPassword_ReturnsFalse_WhenPasswordIsNull()
    {
        var account = new Account { EncryptedPassword = null };

        _service.HasStoredPassword(account).Should().BeFalse();
    }

    [Fact]
    public void HasStoredPassword_ReturnsFalse_WhenPasswordIsEmpty()
    {
        var account = new Account { EncryptedPassword = "" };

        _service.HasStoredPassword(account).Should().BeFalse();
    }

    [Fact]
    public void ClearPassword_RemovesStoredPassword()
    {
        var account = new Account { EncryptedPassword = "ENC:secret" };

        _service.ClearPassword(account);

        account.EncryptedPassword.Should().BeNull();
    }

    [Fact]
    public void StorePassword_WithNullAccount_ThrowsArgumentNullException()
    {
        var act = () => _service.StorePassword(null!, "password");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("account");
    }

    [Fact]
    public void StorePassword_WithNullPassword_ThrowsArgumentException()
    {
        var account = new Account { EmailAddress = "test@example.com" };

        var act = () => _service.StorePassword(account, null!);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("plainPassword");
    }

    [Fact]
    public void StorePassword_WithEmptyPassword_ThrowsArgumentException()
    {
        var account = new Account { EmailAddress = "test@example.com" };

        var act = () => _service.StorePassword(account, "");

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("plainPassword");
    }

    [Fact]
    public void RetrievePassword_WithNullAccount_ThrowsArgumentNullException()
    {
        var act = () => _service.RetrievePassword(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ClearPassword_WithNullAccount_ThrowsArgumentNullException()
    {
        var act = () => _service.ClearPassword(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FullRoundTrip_Store_Has_Retrieve_Clear_HasReturnsFalse()
    {
        var account = new Account { EmailAddress = "roundtrip@example.com" };

        // Initially no password
        _service.HasStoredPassword(account).Should().BeFalse();

        // Store
        _service.StorePassword(account, "round-trip-pw");
        _service.HasStoredPassword(account).Should().BeTrue();
        account.AuthType.Should().Be(AuthType.Password);

        // Retrieve
        var retrieved = _service.RetrievePassword(account);
        retrieved.Should().Be("round-trip-pw");

        // Clear
        _service.ClearPassword(account);
        _service.HasStoredPassword(account).Should().BeFalse();
    }
}
