namespace MailAggregator.Core.Services.Discovery;

using MailAggregator.Core.Models;

/// <summary>
/// Discovers IMAP/SMTP server configuration for an email address
/// using autoconfig XML, Thunderbird ISPDB, and MX record fallback.
/// </summary>
public interface IAutoDiscoveryService
{
    /// <summary>
    /// Discovers IMAP/SMTP server configuration for the given email address.
    /// Returns null if no configuration could be discovered.
    /// </summary>
    Task<ServerConfiguration?> DiscoverAsync(string emailAddress, CancellationToken cancellationToken = default);
}
