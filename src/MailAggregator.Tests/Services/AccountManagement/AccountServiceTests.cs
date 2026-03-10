using FluentAssertions;
using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.AccountManagement;
using MailAggregator.Core.Services.Auth;
using MailAggregator.Core.Services.Discovery;
using MailAggregator.Core.Services.Mail;
using MailAggregator.Core.Services.Sync;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using Moq;
using Serilog;

namespace MailAggregator.Tests.Services.AccountManagement;

public class AccountServiceTests : IDisposable
{
    private readonly MailAggregatorDbContext _dbContext;
    private readonly Mock<IAutoDiscoveryService> _mockAutoDiscovery;
    private readonly Mock<IOAuthService> _mockOAuth;
    private readonly Mock<IPasswordAuthService> _mockPasswordAuth;
    private readonly Mock<IImapConnectionService> _mockImapConnection;
    private readonly Mock<ISyncManager> _mockSyncManager;
    private readonly Mock<IImapConnectionPool> _mockConnectionPool;
    private readonly Mock<ILogger> _mockLogger;
    private readonly AccountService _service;

    private static readonly ServerConfiguration DefaultServerConfig = new()
    {
        ImapHost = "imap.example.com",
        ImapPort = 993,
        ImapEncryption = ConnectionEncryptionType.Ssl,
        SmtpHost = "smtp.example.com",
        SmtpPort = 587,
        SmtpEncryption = ConnectionEncryptionType.StartTls
    };

    public AccountServiceTests()
    {
        var options = new DbContextOptionsBuilder<MailAggregatorDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _dbContext = new MailAggregatorDbContext(options);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();

        _mockAutoDiscovery = new Mock<IAutoDiscoveryService>();
        _mockOAuth = new Mock<IOAuthService>();
        _mockPasswordAuth = new Mock<IPasswordAuthService>();
        _mockImapConnection = new Mock<IImapConnectionService>();
        _mockSyncManager = new Mock<ISyncManager>();
        _mockConnectionPool = new Mock<IImapConnectionPool>();
        _mockLogger = new Mock<ILogger>();

        _service = new AccountService(
            _dbContext,
            _mockAutoDiscovery.Object,
            _mockOAuth.Object,
            _mockPasswordAuth.Object,
            _mockImapConnection.Object,
            _mockSyncManager.Object,
            _mockConnectionPool.Object,
            _mockLogger.Object);
    }

    public void Dispose()
    {
        _dbContext.Database.CloseConnection();
        _dbContext.Dispose();
    }

    #region AddAccountAsync

    [Fact]
    public async Task AddAccountAsync_WithPasswordAuth_HappyPath()
    {
        // Arrange
        var email = "user@example.com";
        var password = "s3cret";

        _mockAutoDiscovery
            .Setup(x => x.DiscoverAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultServerConfig);

        _mockOAuth
            .Setup(x => x.FindProviderByHost(DefaultServerConfig.ImapHost))
            .Returns((OAuthProviderConfig?)null);

        var mockClient = new Mock<ImapClient>();
        mockClient.Setup(c => c.IsConnected).Returns(true);
        mockClient.Setup(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockImapConnection
            .Setup(x => x.ConnectAsync(It.IsAny<Core.Models.Account>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        // Act
        var result = await _service.AddAccountAsync(email, password);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.EmailAddress.Should().Be(email);
        result.ImapHost.Should().Be(DefaultServerConfig.ImapHost);
        result.ImapPort.Should().Be(DefaultServerConfig.ImapPort);
        result.ImapEncryption.Should().Be(DefaultServerConfig.ImapEncryption);
        result.SmtpHost.Should().Be(DefaultServerConfig.SmtpHost);
        result.SmtpPort.Should().Be(DefaultServerConfig.SmtpPort);
        result.SmtpEncryption.Should().Be(DefaultServerConfig.SmtpEncryption);
        result.AuthType.Should().Be(AuthType.Password);
        result.IsEnabled.Should().BeTrue();

        // Verify password was stored
        _mockPasswordAuth.Verify(
            x => x.StorePassword(It.IsAny<Core.Models.Account>(), password), Times.Once);

        // Verify connection was validated
        _mockImapConnection.Verify(
            x => x.ConnectAsync(It.IsAny<Core.Models.Account>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify account was persisted to database
        var savedAccount = await _dbContext.Accounts.FirstOrDefaultAsync(a => a.EmailAddress == email);
        savedAccount.Should().NotBeNull();
        savedAccount!.Id.Should().Be(result.Id);
    }

    [Fact]
    public async Task AddAccountAsync_WithOAuthDetection_SetsAuthTypeAndSkipsConnectionValidation()
    {
        // Arrange
        var email = "user@gmail.com";
        var oauthConfig = new ServerConfiguration
        {
            ImapHost = "imap.gmail.com",
            ImapPort = 993,
            SmtpHost = "smtp.gmail.com",
            SmtpPort = 587
        };

        var oauthProvider = new OAuthProviderConfig
        {
            Name = "Google",
            ServerHosts = new List<string> { "imap.gmail.com", "smtp.gmail.com" },
            ClientId = "test-client-id"
        };

        _mockAutoDiscovery
            .Setup(x => x.DiscoverAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(oauthConfig);

        _mockOAuth
            .Setup(x => x.FindProviderByHost("imap.gmail.com"))
            .Returns(oauthProvider);

        // Act
        var result = await _service.AddAccountAsync(email, null);

        // Assert
        result.Should().NotBeNull();
        result.AuthType.Should().Be(AuthType.OAuth2);
        result.EmailAddress.Should().Be(email);
        result.ImapHost.Should().Be("imap.gmail.com");

        // Password should NOT be stored for OAuth
        _mockPasswordAuth.Verify(
            x => x.StorePassword(It.IsAny<Core.Models.Account>(), It.IsAny<string>()), Times.Never);

        // Connection should NOT be validated for OAuth (no tokens yet)
        _mockImapConnection.Verify(
            x => x.ConnectAsync(It.IsAny<Core.Models.Account>(), It.IsAny<CancellationToken>()), Times.Never);

        // Account should still be saved
        var savedAccount = await _dbContext.Accounts.FirstOrDefaultAsync(a => a.EmailAddress == email);
        savedAccount.Should().NotBeNull();
    }

    [Fact]
    public async Task AddAccountAsync_WhenDiscoveryFails_ThrowsInvalidOperationException()
    {
        // Arrange
        var email = "user@unknown-domain.xyz";

        _mockAutoDiscovery
            .Setup(x => x.DiscoverAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServerConfiguration?)null);

        // Act
        var act = () => _service.AddAccountAsync(email, "password");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*discover*server*configuration*");
    }

    [Fact]
    public async Task AddAccountAsync_WhenConnectionValidationFails_ThrowsAndDoesNotSave()
    {
        // Arrange
        var email = "user@example.com";

        _mockAutoDiscovery
            .Setup(x => x.DiscoverAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultServerConfig);

        _mockOAuth
            .Setup(x => x.FindProviderByHost(DefaultServerConfig.ImapHost))
            .Returns((OAuthProviderConfig?)null);

        _mockImapConnection
            .Setup(x => x.ConnectAsync(It.IsAny<Core.Models.Account>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        // Act
        var act = () => _service.AddAccountAsync(email, "password");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*connect*IMAP*");

        // Verify account was NOT saved to database
        var count = await _dbContext.Accounts.CountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task AddAccountAsync_WithNullEmailAddress_ThrowsArgumentException()
    {
        var act = () => _service.AddAccountAsync(null!, "password");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("emailAddress");
    }

    [Fact]
    public async Task AddAccountAsync_WithEmptyEmailAddress_ThrowsArgumentException()
    {
        var act = () => _service.AddAccountAsync("", "password");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("emailAddress");
    }

    [Fact]
    public async Task AddAccountAsync_PasswordAuth_WithNullPassword_ThrowsArgumentException()
    {
        // Arrange - non-OAuth provider
        _mockAutoDiscovery
            .Setup(x => x.DiscoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultServerConfig);

        _mockOAuth
            .Setup(x => x.FindProviderByHost(It.IsAny<string>()))
            .Returns((OAuthProviderConfig?)null);

        // Act
        var act = () => _service.AddAccountAsync("user@example.com", null);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("password");
    }

    [Fact]
    public async Task AddAccountAsync_WithExplicitPasswordAuthType_DoesNotOverrideToOAuth()
    {
        // Arrange — OAuth-capable host, but caller explicitly requests Password auth
        var email = "user@company.com";
        var password = "s3cret";
        var oauthHostConfig = new ServerConfiguration
        {
            ImapHost = "outlook.office365.com",
            ImapPort = 993,
            ImapEncryption = ConnectionEncryptionType.Ssl,
            SmtpHost = "smtp-mail.outlook.com",
            SmtpPort = 587,
            SmtpEncryption = ConnectionEncryptionType.StartTls
        };

        // FindProviderByHost would return a provider, but the explicit authType should win
        _mockOAuth
            .Setup(x => x.FindProviderByHost("outlook.office365.com"))
            .Returns(new OAuthProviderConfig { Name = "Microsoft", ServerHosts = new List<string> { "outlook.office365.com" }, ClientId = "test" });

        var mockClient = new Mock<ImapClient>();
        mockClient.Setup(c => c.IsConnected).Returns(true);
        mockClient.Setup(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockImapConnection
            .Setup(x => x.ConnectAsync(It.IsAny<Core.Models.Account>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        // Act — pass explicit AuthType.Password
        var result = await _service.AddAccountAsync(email, password, oauthHostConfig, AuthType.Password);

        // Assert — should be Password, NOT OAuth2
        result.AuthType.Should().Be(AuthType.Password);
        _mockPasswordAuth.Verify(
            x => x.StorePassword(It.IsAny<Core.Models.Account>(), password), Times.Once);
        _mockImapConnection.Verify(
            x => x.ConnectAsync(It.IsAny<Core.Models.Account>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddAccountAsync_WithExplicitOAuthAuthType_SetsOAuth2()
    {
        // Arrange
        var email = "user@gmail.com";
        var oauthHostConfig = new ServerConfiguration
        {
            ImapHost = "imap.gmail.com",
            ImapPort = 993,
            SmtpHost = "smtp.gmail.com",
            SmtpPort = 587
        };

        // Act — pass explicit AuthType.OAuth2
        var result = await _service.AddAccountAsync(email, null, oauthHostConfig, AuthType.OAuth2);

        // Assert
        result.AuthType.Should().Be(AuthType.OAuth2);
        _mockPasswordAuth.Verify(
            x => x.StorePassword(It.IsAny<Core.Models.Account>(), It.IsAny<string>()), Times.Never);
        _mockImapConnection.Verify(
            x => x.ConnectAsync(It.IsAny<Core.Models.Account>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddAccountAsync_DuplicateEmail_ThrowsInvalidOperationException()
    {
        // Arrange - pre-insert an account
        _dbContext.Accounts.Add(new Core.Models.Account
        {
            EmailAddress = "duplicate@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var act = () => _service.AddAccountAsync("duplicate@example.com", "password");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    #endregion

    #region UpdateAccountAsync

    [Fact]
    public async Task UpdateAccountAsync_HappyPath_UpdatesAndSaves()
    {
        // Arrange - insert an account first
        var account = new Core.Models.Account
        {
            EmailAddress = "update@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        };
        _dbContext.Accounts.Add(account);
        await _dbContext.SaveChangesAsync();

        // Modify the account
        account.DisplayName = "Updated Display Name";
        account.ProxyHost = "proxy.example.com";
        account.ProxyPort = 1080;

        // Act
        var result = await _service.UpdateAccountAsync(account);

        // Assert
        result.Should().NotBeNull();
        result.DisplayName.Should().Be("Updated Display Name");
        result.ProxyHost.Should().Be("proxy.example.com");
        result.ProxyPort.Should().Be(1080);

        // Verify persisted
        var fromDb = await _dbContext.Accounts.FirstAsync(a => a.Id == account.Id);
        fromDb.DisplayName.Should().Be("Updated Display Name");
        fromDb.ProxyHost.Should().Be("proxy.example.com");
    }

    [Fact]
    public async Task UpdateAccountAsync_WithNullAccount_ThrowsArgumentNullException()
    {
        var act = () => _service.UpdateAccountAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region DeleteAccountAsync

    [Fact]
    public async Task DeleteAccountAsync_HappyPath_RemovesAccountAndRelatedEntities()
    {
        // Arrange - create account with folder, message, and attachment
        var account = new Core.Models.Account
        {
            EmailAddress = "delete@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        };
        _dbContext.Accounts.Add(account);
        await _dbContext.SaveChangesAsync();

        var folder = new MailFolder
        {
            AccountId = account.Id,
            Name = "INBOX",
            FullName = "INBOX",
            UidValidity = 1
        };
        _dbContext.Folders.Add(folder);
        await _dbContext.SaveChangesAsync();

        var message = new EmailMessage
        {
            AccountId = account.Id,
            FolderId = folder.Id,
            Uid = 1,
            FromAddress = "sender@example.com",
            ToAddresses = "delete@example.com",
            DateSent = DateTimeOffset.UtcNow
        };
        _dbContext.Messages.Add(message);
        await _dbContext.SaveChangesAsync();

        var attachment = new EmailAttachment
        {
            EmailMessageId = message.Id,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            Size = 1024
        };
        _dbContext.Attachments.Add(attachment);
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(account.Id);

        // Assert - all related entities should be cascade-deleted
        (await _dbContext.Accounts.CountAsync()).Should().Be(0);
        (await _dbContext.Folders.CountAsync()).Should().Be(0);
        (await _dbContext.Messages.CountAsync()).Should().Be(0);
        (await _dbContext.Attachments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAccountAsync_StopsSyncAndReleasesPool()
    {
        // Arrange
        var account = new Core.Models.Account
        {
            EmailAddress = "cleanup@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        };
        _dbContext.Accounts.Add(account);
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.DeleteAccountAsync(account.Id);

        // Assert - verify cleanup methods were called
        _mockSyncManager.Verify(x => x.StopAccountSyncAsync(account.Id), Times.Once);
        _mockConnectionPool.Verify(x => x.RemoveAccount(account.Id), Times.Once);
    }

    [Fact]
    public async Task DeleteAccountAsync_WithInvalidId_ThrowsInvalidOperationException()
    {
        var act = () => _service.DeleteAccountAsync(999);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*not found*");
    }

    #endregion

    #region GetAllAccountsAsync

    [Fact]
    public async Task GetAllAccountsAsync_ReturnsAllAccounts()
    {
        // Arrange
        _dbContext.Accounts.Add(new Core.Models.Account
        {
            EmailAddress = "a@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        });
        _dbContext.Accounts.Add(new Core.Models.Account
        {
            EmailAddress = "b@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        });
        _dbContext.Accounts.Add(new Core.Models.Account
        {
            EmailAddress = "c@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.GetAllAccountsAsync();

        // Assert
        result.Should().HaveCount(3);
        result.Select(a => a.EmailAddress).Should()
            .Contain(new[] { "a@example.com", "b@example.com", "c@example.com" });
    }

    [Fact]
    public async Task GetAllAccountsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var result = await _service.GetAllAccountsAsync();

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    #endregion

    #region GetAccountByIdAsync

    [Fact]
    public async Task GetAccountByIdAsync_ExistingId_ReturnsAccount()
    {
        // Arrange
        var account = new Core.Models.Account
        {
            EmailAddress = "find@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        };
        _dbContext.Accounts.Add(account);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.GetAccountByIdAsync(account.Id);

        // Assert
        result.Should().NotBeNull();
        result!.EmailAddress.Should().Be("find@example.com");
        result.Id.Should().Be(account.Id);
    }

    [Fact]
    public async Task GetAccountByIdAsync_NonExistingId_ReturnsNull()
    {
        var result = await _service.GetAccountByIdAsync(999);

        result.Should().BeNull();
    }

    #endregion

    #region ValidateConnectionAsync

    [Fact]
    public async Task ValidateConnectionAsync_Success_ReturnsTrue()
    {
        // Arrange
        var account = new Core.Models.Account
        {
            EmailAddress = "validate@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        };

        var mockClient = new Mock<ImapClient>();
        mockClient.Setup(c => c.IsConnected).Returns(true);
        mockClient.Setup(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockImapConnection
            .Setup(x => x.ConnectAsync(account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        // Act
        var result = await _service.ValidateConnectionAsync(account);

        // Assert
        result.Should().BeTrue();
        _mockImapConnection.Verify(
            x => x.ConnectAsync(account, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateConnectionAsync_Failure_ReturnsFalse()
    {
        // Arrange
        var account = new Core.Models.Account
        {
            EmailAddress = "fail@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        };

        _mockImapConnection
            .Setup(x => x.ConnectAsync(account, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        // Act
        var result = await _service.ValidateConnectionAsync(account);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateConnectionAsync_WithNullAccount_ThrowsArgumentNullException()
    {
        var act = () => _service.ValidateConnectionAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion
}
