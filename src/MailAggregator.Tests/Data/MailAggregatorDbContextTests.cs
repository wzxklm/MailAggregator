using FluentAssertions;
using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MailAggregator.Tests.Data;

public class MailAggregatorDbContextTests : IDisposable
{
    private readonly MailAggregatorDbContext _context;

    public MailAggregatorDbContextTests()
    {
        var options = new DbContextOptionsBuilder<MailAggregatorDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _context = new MailAggregatorDbContext(options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
    }

    private async Task<(Account Account, MailFolder Folder)> CreateAccountWithFolderAsync(
        string email = "test@example.com")
    {
        var account = new Account
        {
            EmailAddress = email,
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        };
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        var folder = new MailFolder
        {
            AccountId = account.Id,
            Name = "INBOX",
            FullName = "INBOX",
            UidValidity = 1
        };
        _context.Folders.Add(folder);
        await _context.SaveChangesAsync();

        return (account, folder);
    }

    [Fact]
    public async Task CanInsertAndRetrieveAccount()
    {
        var account = new Account
        {
            EmailAddress = "test@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com",
            AuthType = AuthType.Password
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        var retrieved = await _context.Accounts.FirstAsync();
        retrieved.EmailAddress.Should().Be("test@example.com");
        retrieved.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SaveChanges_SetsTimestampsOnInsert()
    {
        var before = DateTimeOffset.UtcNow;
        var account = new Account
        {
            EmailAddress = "ts@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        };
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        account.CreatedAt.Should().BeOnOrAfter(before);
        account.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task CanInsertFolderWithAccount()
    {
        var (account, folder) = await CreateAccountWithFolderAsync();

        var retrieved = await _context.Folders
            .Include(f => f.Account)
            .FirstAsync();
        retrieved.Name.Should().Be("INBOX");
        retrieved.Account.EmailAddress.Should().Be("test@example.com");
    }

    [Fact]
    public async Task CanInsertMessageWithAttachments()
    {
        var (account, folder) = await CreateAccountWithFolderAsync();

        var message = new EmailMessage
        {
            AccountId = account.Id,
            FolderId = folder.Id,
            Uid = 100,
            FromAddress = "sender@example.com",
            ToAddresses = "test@example.com",
            Subject = "Test Email",
            DateSent = DateTimeOffset.UtcNow,
            HasAttachments = true
        };
        _context.Messages.Add(message);
        await _context.SaveChangesAsync();

        var attachment = new EmailAttachment
        {
            EmailMessageId = message.Id,
            FileName = "report.pdf",
            ContentType = "application/pdf",
            Size = 1024
        };
        _context.Attachments.Add(attachment);
        await _context.SaveChangesAsync();

        var retrieved = await _context.Messages
            .Include(m => m.Attachments)
            .FirstAsync();
        retrieved.Subject.Should().Be("Test Email");
        retrieved.Attachments.Should().HaveCount(1);
        retrieved.Attachments.First().FileName.Should().Be("report.pdf");
    }

    [Fact]
    public async Task CascadeDelete_DeletingAccount_RemovesFoldersAndMessages()
    {
        var (account, folder) = await CreateAccountWithFolderAsync("delete@example.com");

        var message = new EmailMessage
        {
            AccountId = account.Id,
            FolderId = folder.Id,
            Uid = 1,
            FromAddress = "sender@example.com",
            ToAddresses = "delete@example.com",
            DateSent = DateTimeOffset.UtcNow
        };
        _context.Messages.Add(message);
        await _context.SaveChangesAsync();

        _context.Accounts.Remove(account);
        await _context.SaveChangesAsync();

        (await _context.Accounts.CountAsync()).Should().Be(0);
        (await _context.Folders.CountAsync()).Should().Be(0);
        (await _context.Messages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UniqueIndex_FolderUid_PreventsDuplicateMessages()
    {
        var (account, folder) = await CreateAccountWithFolderAsync();

        _context.Messages.Add(new EmailMessage
        {
            AccountId = account.Id, FolderId = folder.Id, Uid = 42,
            FromAddress = "a@b.com", ToAddresses = "c@d.com",
            DateSent = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        _context.Messages.Add(new EmailMessage
        {
            AccountId = account.Id, FolderId = folder.Id, Uid = 42,
            FromAddress = "x@y.com", ToAddresses = "c@d.com",
            DateSent = DateTimeOffset.UtcNow
        });

        var act = () => _context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
