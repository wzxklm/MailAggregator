namespace MailAggregator.Core.Models;

/// <summary>
/// Represents a discovered mail server configuration from AutoDiscovery.
/// </summary>
public class ServerConfiguration
{
    // IMAP settings
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public ConnectionEncryptionType ImapEncryption { get; set; } = ConnectionEncryptionType.Ssl;

    // SMTP settings
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public ConnectionEncryptionType SmtpEncryption { get; set; } = ConnectionEncryptionType.StartTls;
}
