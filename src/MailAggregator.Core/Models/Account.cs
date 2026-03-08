namespace MailAggregator.Core.Models;

public class Account
{
    public int Id { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    // IMAP settings
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public ConnectionEncryptionType ImapEncryption { get; set; } = ConnectionEncryptionType.Ssl;

    // SMTP settings
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public ConnectionEncryptionType SmtpEncryption { get; set; } = ConnectionEncryptionType.StartTls;

    // Authentication
    public AuthType AuthType { get; set; } = AuthType.Password;
    public string? EncryptedPassword { get; set; }
    public string? EncryptedAccessToken { get; set; }
    public string? EncryptedRefreshToken { get; set; }
    public DateTimeOffset? OAuthTokenExpiry { get; set; }

    // SOCKS5 proxy (per-account)
    public string? ProxyHost { get; set; }
    public int? ProxyPort { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<MailFolder> Folders { get; set; } = new List<MailFolder>();
    public ICollection<EmailMessage> Messages { get; set; } = new List<EmailMessage>();
}
