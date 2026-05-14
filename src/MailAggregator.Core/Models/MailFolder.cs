namespace MailAggregator.Core.Models;

public class MailFolder
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public SpecialFolderType SpecialUse { get; set; } = SpecialFolderType.None;
    public uint UidValidity { get; set; }
    public uint MaxUid { get; set; }
    public int MessageCount { get; set; }
    public int UnreadCount { get; set; }

    // Navigation properties
    public Account Account { get; set; } = null!;
    public ICollection<EmailMessage> Messages { get; set; } = new List<EmailMessage>();
}
