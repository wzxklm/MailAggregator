using FluentAssertions;
using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using MailAggregator.Core.Services.TwoFactor;
using Microsoft.EntityFrameworkCore;
using Moq;
using Serilog;

namespace MailAggregator.Tests.Services.TwoFactor;

public class TwoFactorAccountServiceTests : IDisposable
{
    private readonly MailAggregatorDbContext _dbContext;
    private readonly CredentialEncryptionService _encryptionService;
    private readonly TwoFactorCodeService _codeService;
    private readonly TwoFactorAccountService _service;
    private readonly string _tempDir;

    private const string TestSecret = "JBSWY3DPEHPK3PXP";
    private const string TestIssuer = "GitHub";
    private const string TestLabel = "user@example.com";

    public TwoFactorAccountServiceTests()
    {
        var options = new DbContextOptionsBuilder<MailAggregatorDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _dbContext = new MailAggregatorDbContext(options);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();

        _tempDir = Path.Combine(Path.GetTempPath(), $"mail_agg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var keyPath = Path.Combine(_tempDir, "test.key");
        _encryptionService = new CredentialEncryptionService(new DevKeyProtector(), keyPath);

        _codeService = new TwoFactorCodeService();
        var mockLogger = new Mock<ILogger>();

        _service = new TwoFactorAccountService(
            _dbContext,
            _encryptionService,
            _codeService,
            mockLogger.Object);
    }

    public void Dispose()
    {
        _dbContext.Database.CloseConnection();
        _dbContext.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    #region AddAsync

    [Fact]
    public async Task AddAsync_HappyPath_SavesAccountWithEncryptedSecret()
    {
        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret);

        account.Id.Should().BeGreaterThan(0);
        account.Issuer.Should().Be(TestIssuer);
        account.Label.Should().Be(TestLabel);
        account.EncryptedSecret.Should().NotBe(TestSecret, "secret must be encrypted");
        account.Algorithm.Should().Be(OtpAlgorithm.Sha1);
        account.Digits.Should().Be(6);
        account.Period.Should().Be(30);
    }

    [Fact]
    public async Task AddAsync_CustomParams_SavesCorrectly()
    {
        var account = await _service.AddAsync(
            TestIssuer, TestLabel, TestSecret,
            OtpAlgorithm.Sha256, digits: 8, period: 60);

        account.Algorithm.Should().Be(OtpAlgorithm.Sha256);
        account.Digits.Should().Be(8);
        account.Period.Should().Be(60);
    }

    [Fact]
    public async Task AddAsync_SetsTimestamps()
    {
        var before = DateTimeOffset.UtcNow;

        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret);

        account.CreatedAt.Should().BeOnOrAfter(before);
        account.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task AddAsync_EncryptionRoundTrip_SecretDecryptsCorrectly()
    {
        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret);

        var decrypted = _service.GetDecryptedSecret(account);

        decrypted.Should().Be(TestSecret);
    }

    [Fact]
    public async Task AddAsync_EmptyIssuer_ThrowsArgumentException()
    {
        var act = () => _service.AddAsync("", TestLabel, TestSecret);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddAsync_EmptyLabel_ThrowsArgumentException()
    {
        var act = () => _service.AddAsync(TestIssuer, "", TestSecret);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddAsync_EmptySecret_ThrowsArgumentException()
    {
        var act = () => _service.AddAsync(TestIssuer, TestLabel, "");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddAsync_NormalizesSecretToUppercase()
    {
        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret.ToLowerInvariant());

        var decrypted = _service.GetDecryptedSecret(account);

        decrypted.Should().Be(TestSecret);
    }

    #endregion

    #region AddFromUriAsync

    [Fact]
    public async Task AddFromUriAsync_ValidUri_CreatesAccount()
    {
        var uri = "otpauth://totp/GitHub:user@example.com?secret=JBSWY3DPEHPK3PXP&issuer=GitHub&algorithm=SHA256&digits=8&period=60";

        var account = await _service.AddFromUriAsync(uri);

        account.Issuer.Should().Be("GitHub");
        account.Label.Should().Be("user@example.com");
        account.Algorithm.Should().Be(OtpAlgorithm.Sha256);
        account.Digits.Should().Be(8);
        account.Period.Should().Be(60);

        var decrypted = _service.GetDecryptedSecret(account);
        decrypted.Should().Be("JBSWY3DPEHPK3PXP");
    }

    [Fact]
    public async Task AddFromUriAsync_InvalidUri_ThrowsFormatException()
    {
        var act = () => _service.AddFromUriAsync("not-a-uri");

        await act.Should().ThrowAsync<FormatException>();
    }

    #endregion

    #region UpdateAsync

    [Fact]
    public async Task UpdateAsync_UpdatesIssuerAndLabel()
    {
        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret);

        var updated = await _service.UpdateAsync(account.Id, "NewIssuer", "newlabel");

        updated.Issuer.Should().Be("NewIssuer");
        updated.Label.Should().Be("newlabel");
    }

    [Fact]
    public async Task UpdateAsync_PreservesEncryptedSecret()
    {
        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret);
        var originalSecret = account.EncryptedSecret;

        var updated = await _service.UpdateAsync(account.Id, "NewIssuer", "newlabel");

        updated.EncryptedSecret.Should().Be(originalSecret);
    }

    [Fact]
    public async Task UpdateAsync_NonexistentId_ThrowsInvalidOperationException()
    {
        var act = () => _service.UpdateAsync(999, "Issuer", "Label");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_EmptyIssuer_ThrowsArgumentException()
    {
        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret);

        var act = () => _service.UpdateAsync(account.Id, "", "label");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region DeleteAsync

    [Fact]
    public async Task DeleteAsync_RemovesAccount()
    {
        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret);

        await _service.DeleteAsync(account.Id);

        var all = await _service.GetAllAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_NonexistentId_ThrowsInvalidOperationException()
    {
        var act = () => _service.DeleteAsync(999);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region GetAllAsync

    [Fact]
    public async Task GetAllAsync_Empty_ReturnsEmptyList()
    {
        var result = await _service.GetAllAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_MultipleAccounts_ReturnsOrderedByCreatedAt()
    {
        await _service.AddAsync("First", "first@test.com", TestSecret);
        await _service.AddAsync("Second", "second@test.com", TestSecret);
        await _service.AddAsync("Third", "third@test.com", TestSecret);

        var result = await _service.GetAllAsync();

        result.Should().HaveCount(3);
        result[0].Issuer.Should().Be("First");
        result[1].Issuer.Should().Be("Second");
        result[2].Issuer.Should().Be("Third");
    }

    #endregion

    #region GetDecryptedSecret

    [Fact]
    public async Task GetDecryptedSecret_ReturnsOriginalSecret()
    {
        var account = await _service.AddAsync(TestIssuer, TestLabel, TestSecret);

        // Re-fetch from DB to simulate real usage
        var fetched = (await _service.GetAllAsync()).First();
        var decrypted = _service.GetDecryptedSecret(fetched);

        decrypted.Should().Be(TestSecret);
    }

    [Fact]
    public void GetDecryptedSecret_NullAccount_ThrowsArgumentNullException()
    {
        var act = () => _service.GetDecryptedSecret(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetDecryptedSecret_EmptyEncryptedSecret_ThrowsInvalidOperationException()
    {
        var account = new TwoFactorAccount { EncryptedSecret = "" };

        var act = () => _service.GetDecryptedSecret(account);

        act.Should().Throw<InvalidOperationException>();
    }

    #endregion
}
