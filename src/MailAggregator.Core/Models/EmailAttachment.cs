namespace MailAggregator.Core.Models;

public class EmailAttachment
{
    public long Id { get; set; }
    public long EmailMessageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long Size { get; set; }
    public string? LocalPath { get; set; }
    public string? ContentId { get; set; }

    // Navigation property
    public EmailMessage EmailMessage { get; set; } = null!;
}
