using MailAggregator.Core.Models;
using MailKit.Net.Imap;

namespace MailAggregator.Core.Services.Mail;

public interface IImapConnectionService
{
    /// <summary>
    /// Connects and authenticates an IMAP client for the given account.
    /// Supports XOAUTH2 and PLAIN authentication, SOCKS5 proxy, and retry with exponential backoff.
    /// The caller is responsible for disposing the returned client.
    /// </summary>
    Task<ImapClient> ConnectAsync(Account account, CancellationToken cancellationToken = default);
}
