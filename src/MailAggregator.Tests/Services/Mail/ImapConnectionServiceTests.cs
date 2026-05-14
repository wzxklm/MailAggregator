using FluentAssertions;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Mail;
using MailKit.Security;

namespace MailAggregator.Tests.Services.Mail;

public class MailConnectionHelperTests
{
    [Theory]
    [InlineData(ConnectionEncryptionType.Ssl, SecureSocketOptions.SslOnConnect)]
    [InlineData(ConnectionEncryptionType.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(ConnectionEncryptionType.None, SecureSocketOptions.None)]
    public void GetSecureSocketOptions_MapsCorrectly(ConnectionEncryptionType input, SecureSocketOptions expected)
    {
        MailConnectionHelper.GetSecureSocketOptions(input).Should().Be(expected);
    }

    [Fact]
    public void GetSecureSocketOptions_UnknownValue_ReturnsAuto()
    {
        MailConnectionHelper.GetSecureSocketOptions((ConnectionEncryptionType)99).Should().Be(SecureSocketOptions.Auto);
    }
}
