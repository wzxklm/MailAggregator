using FluentAssertions;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Mail;
using MailKit;

namespace MailAggregator.Tests.Services.Mail;

public class EmailSyncServiceTests
{
    [Fact]
    public void MapSpecialUse_Inbox()
    {
        EmailSyncService.MapSpecialUse(FolderAttributes.Inbox).Should().Be(SpecialFolderType.Inbox);
    }

    [Fact]
    public void MapSpecialUse_Sent()
    {
        EmailSyncService.MapSpecialUse(FolderAttributes.Sent).Should().Be(SpecialFolderType.Sent);
    }

    [Fact]
    public void MapSpecialUse_Drafts()
    {
        EmailSyncService.MapSpecialUse(FolderAttributes.Drafts).Should().Be(SpecialFolderType.Drafts);
    }

    [Fact]
    public void MapSpecialUse_Trash()
    {
        EmailSyncService.MapSpecialUse(FolderAttributes.Trash).Should().Be(SpecialFolderType.Trash);
    }

    [Fact]
    public void MapSpecialUse_Junk()
    {
        EmailSyncService.MapSpecialUse(FolderAttributes.Junk).Should().Be(SpecialFolderType.Junk);
    }

    [Fact]
    public void MapSpecialUse_Archive()
    {
        EmailSyncService.MapSpecialUse(FolderAttributes.Archive).Should().Be(SpecialFolderType.Archive);
    }

    [Fact]
    public void MapSpecialUse_None()
    {
        EmailSyncService.MapSpecialUse(FolderAttributes.None).Should().Be(SpecialFolderType.None);
    }

    [Fact]
    public void MapSpecialUse_CombinedFlags_PrioritizesInbox()
    {
        // When multiple special-use flags are present, Inbox takes priority
        var flags = FolderAttributes.Inbox | FolderAttributes.HasChildren;
        EmailSyncService.MapSpecialUse(flags).Should().Be(SpecialFolderType.Inbox);
    }

    [Fact]
    public void MapSpecialUse_NonSpecialFlag_ReturnsNone()
    {
        EmailSyncService.MapSpecialUse(FolderAttributes.HasChildren).Should().Be(SpecialFolderType.None);
    }
}
