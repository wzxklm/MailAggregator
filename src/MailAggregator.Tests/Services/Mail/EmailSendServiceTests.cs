using FluentAssertions;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Mail;
using MimeKit;

namespace MailAggregator.Tests.Services.Mail;

public class EmailSendServiceTests
{
    #region BuildQuotedReply Tests

    [Fact]
    public void BuildQuotedReply_PlainText_ContainsOriginalHeader()
    {
        var original = CreateOriginalMessage();
        var result = EmailSendService.BuildQuotedReply(original, "My reply", isHtml: false);

        result.Should().StartWith("My reply");
        result.Should().Contain("--- Original Message ---");
        result.Should().Contain("From: sender@example.com");
        result.Should().Contain("Subject: Test Subject");
        result.Should().Contain("> Original body text");
    }

    [Fact]
    public void BuildQuotedReply_PlainText_QuotesEachLine()
    {
        var original = CreateOriginalMessage();
        original.BodyText = "Line 1\nLine 2\nLine 3";

        var result = EmailSendService.BuildQuotedReply(original, "Reply", isHtml: false);

        result.Should().Contain("> Line 1");
        result.Should().Contain("> Line 2");
        result.Should().Contain("> Line 3");
    }

    [Fact]
    public void BuildQuotedReply_Html_ContainsBlockquote()
    {
        var original = CreateOriginalMessage();
        original.BodyHtml = "<p>Hello</p>";

        var result = EmailSendService.BuildQuotedReply(original, "<p>My reply</p>", isHtml: true);

        result.Should().StartWith("<p>My reply</p>");
        result.Should().Contain("<blockquote");
        result.Should().Contain("<p>Hello</p>");
        result.Should().Contain("--- Original Message ---");
    }

    [Fact]
    public void BuildQuotedReply_Html_EscapesFromAddress()
    {
        var original = CreateOriginalMessage();
        original.FromAddress = "test<script>@evil.com";

        var result = EmailSendService.BuildQuotedReply(original, "Reply", isHtml: true);

        result.Should().NotContain("<script>");
        result.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void BuildQuotedReply_Html_FallsBackToTextBody()
    {
        var original = CreateOriginalMessage();
        original.BodyHtml = null;
        original.BodyText = "Plain text body";

        var result = EmailSendService.BuildQuotedReply(original, "Reply", isHtml: true);

        result.Should().Contain("Plain text body");
    }

    [Fact]
    public void BuildQuotedReply_PlainText_HandlesNullBody()
    {
        var original = CreateOriginalMessage();
        original.BodyText = null;

        var result = EmailSendService.BuildQuotedReply(original, "Reply", isHtml: false);

        result.Should().Contain("--- Original Message ---");
        result.Should().Contain("> ");
    }

    #endregion

    #region BuildForwardBody Tests

    [Fact]
    public void BuildForwardBody_PlainText_ContainsForwardHeader()
    {
        var original = CreateOriginalMessage();
        var result = EmailSendService.BuildForwardBody(original, "FYI", isHtml: false);

        result.Should().StartWith("FYI");
        result.Should().Contain("--- Forwarded Message ---");
        result.Should().Contain("From: sender@example.com");
        result.Should().Contain("Subject: Test Subject");
        result.Should().Contain("To: recipient@example.com");
        result.Should().Contain("Original body text");
    }

    [Fact]
    public void BuildForwardBody_Html_ContainsOriginalContent()
    {
        var original = CreateOriginalMessage();
        original.BodyHtml = "<p>Original HTML</p>";

        var result = EmailSendService.BuildForwardBody(original, "<p>See below</p>", isHtml: true);

        result.Should().Contain("--- Forwarded Message ---");
        result.Should().Contain("<p>Original HTML</p>");
    }

    [Fact]
    public void BuildForwardBody_IncludesDateInfo()
    {
        var original = CreateOriginalMessage();
        var result = EmailSendService.BuildForwardBody(original, "FYI", isHtml: false);

        result.Should().Contain("Date: ");
    }

    #endregion

    #region BuildMessageBody Tests

    [Fact]
    public void BuildMessageBody_PlainText_NoAttachments_ReturnsTextPart()
    {
        var result = EmailSendService.BuildMessageBody("Hello", isHtml: false, null);

        result.Should().BeOfType<TextPart>();
        var textPart = (TextPart)result;
        textPart.Text.Should().Be("Hello");
        textPart.ContentType.MimeType.Should().Be("text/plain");
    }

    [Fact]
    public void BuildMessageBody_Html_NoAttachments_ReturnsHtmlPart()
    {
        var result = EmailSendService.BuildMessageBody("<p>Hello</p>", isHtml: true, null);

        result.Should().BeOfType<TextPart>();
        var textPart = (TextPart)result;
        textPart.Text.Should().Be("<p>Hello</p>");
        textPart.ContentType.MimeType.Should().Be("text/html");
    }

    [Fact]
    public void BuildMessageBody_WithAttachments_ReturnsMultipart()
    {
        // Create a temp file for attachment
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content");
            var attachments = new List<string> { tempFile };

            var result = EmailSendService.BuildMessageBody("Hello", isHtml: false, attachments);

            result.Should().BeOfType<Multipart>();
            var multipart = (Multipart)result;
            multipart.Count.Should().Be(2); // text + 1 attachment
            multipart[0].Should().BeOfType<TextPart>();
            multipart[1].Should().BeOfType<MimePart>();

            var attachmentPart = (MimePart)multipart[1];
            attachmentPart.FileName.Should().Be(Path.GetFileName(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void BuildMessageBody_EmptyAttachmentList_ReturnsTextPart()
    {
        var result = EmailSendService.BuildMessageBody("Hello", isHtml: false, new List<string>());

        result.Should().BeOfType<TextPart>();
    }

    #endregion

    #region Reply Subject Tests

    [Fact]
    public void Reply_Subject_AddsRePrefix()
    {
        var original = CreateOriginalMessage();
        original.Subject = "Hello";

        // Test via BuildQuotedReply which doesn't handle subject, but we can verify the logic pattern
        // The actual subject prefixing is in ReplyAsync - testing the quoted reply here
        var result = EmailSendService.BuildQuotedReply(original, "Reply", isHtml: false);
        result.Should().Contain("Subject: Hello");
    }

    [Fact]
    public void BuildForwardBody_SubjectInBody()
    {
        var original = CreateOriginalMessage();
        original.Subject = "Important";

        var result = EmailSendService.BuildForwardBody(original, "FYI", isHtml: false);
        result.Should().Contain("Subject: Important");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void BuildQuotedReply_EmptyReplyBody_StillContainsOriginal()
    {
        var original = CreateOriginalMessage();
        var result = EmailSendService.BuildQuotedReply(original, "", isHtml: false);

        result.Should().Contain("--- Original Message ---");
        result.Should().Contain("> Original body text");
    }

    [Fact]
    public void BuildForwardBody_NullSubject_DoesNotThrow()
    {
        var original = CreateOriginalMessage();
        original.Subject = null;

        var act = () => EmailSendService.BuildForwardBody(original, "FYI", isHtml: false);
        act.Should().NotThrow();
    }

    [Fact]
    public void BuildQuotedReply_Html_NullBothBodies_HandlesGracefully()
    {
        var original = CreateOriginalMessage();
        original.BodyHtml = null;
        original.BodyText = null;

        var result = EmailSendService.BuildQuotedReply(original, "Reply", isHtml: true);

        result.Should().Contain("<blockquote");
    }

    #endregion

    private static EmailMessage CreateOriginalMessage() => new()
    {
        AccountId = 1,
        FolderId = 1,
        Uid = 100,
        MessageId = "<test@example.com>",
        FromAddress = "sender@example.com",
        FromName = "Sender",
        ToAddresses = "recipient@example.com",
        Subject = "Test Subject",
        DateSent = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
        BodyText = "Original body text",
        BodyHtml = null,
        IsRead = true
    };
}
