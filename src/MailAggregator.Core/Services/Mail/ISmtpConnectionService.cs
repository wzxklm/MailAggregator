using MailAggregator.Core.Models;
using MailKit.Net.Smtp;

namespace MailAggregator.Core.Services.Mail;

public interface ISmtpConnectionService
{
    /// <summary>
    /// Connects and authenticates an SMTP client for the given account.
    /// Supports XOAUTH2 and PLAIN authentication, SOCKS5 proxy, and retry with exponential backoff.
    /// The caller is responsible for disposing the returned client.
    /// </summary>
    Task<SmtpClient> ConnectAsync(Account account, CancellationToken cancellationToken = default);
}
