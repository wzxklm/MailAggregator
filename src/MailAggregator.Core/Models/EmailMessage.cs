namespace MailAggregator.Core.Models;

public class EmailMessage
{
    public long Id { get; set; }
    public int AccountId { get; set; }
    public int FolderId { get; set; }
    public uint Uid { get; set; }

    // Message headers
    public string? MessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }

    // Addresses
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string ToAddresses { get; set; } = string.Empty;
    public string? CcAddresses { get; set; }
    public string? BccAddresses { get; set; }

    // Content
    public string? Subject { get; set; }
    public DateTimeOffset DateSent { get; set; }
    public string? PreviewText { get; set; }
    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }

    // Status
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }

    public DateTimeOffset CachedAt { get; set; }

    // Navigation properties
    public Account Account { get; set; } = null!;
    public MailFolder Folder { get; set; } = null!;
    public ICollection<EmailAttachment> Attachments { get; set; } = new List<EmailAttachment>();
}
