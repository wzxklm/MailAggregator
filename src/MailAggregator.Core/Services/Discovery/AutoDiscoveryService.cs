using System.Xml.Linq;
using DnsClient;
using DnsClient.Protocol;
using MailAggregator.Core.Models;
using Serilog;

namespace MailAggregator.Core.Services.Discovery;

/// <summary>
/// Discovers IMAP/SMTP server configuration using a 6-level fallback:
/// 1. autoconfig.{domain}/mail/config-v1.1.xml
/// 2. {domain}/.well-known/autoconfig/mail/config-v1.1.xml
/// 3. Thunderbird ISPDB (autoconfig.thunderbird.net)
/// 4. MX record DNS lookup, then retry levels 1-3 with MX domain
/// 5. RFC 6186 SRV records (_imaps._tcp, _submission._tcp)
/// 6. Return null (UI will guide manual config)
/// </summary>
public class AutoDiscoveryService : IAutoDiscoveryService
{
    private readonly HttpClient _httpClient;
    private readonly ILookupClient _dnsClient;
    private readonly ILogger _logger;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Common country-code second-level domains (ccSLDs) where the base domain
    /// requires three labels instead of two (e.g., "yahoo.co.uk" not "co.uk").
    /// </summary>
    internal static readonly HashSet<string> TwoLevelTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "co.jp", "co.kr", "co.nz", "co.za", "co.in", "co.id", "co.il", "co.th",
        "com.au", "com.br", "com.cn", "com.hk", "com.mx", "com.my", "com.ph", "com.sg", "com.tw", "com.vn",
        "net.au", "net.cn", "net.nz",
        "org.au", "org.cn", "org.nz", "org.uk",
        "ac.uk", "gov.uk", "gov.au", "edu.au", "edu.cn",
        "ne.jp", "or.jp", "ac.jp",
        "com.ar", "com.co", "com.pe", "com.tr"
    };

    public AutoDiscoveryService(HttpClient httpClient, ILogger logger)
        : this(httpClient, new LookupClient(new LookupClientOptions { Timeout = DnsTimeout }), logger)
    {
    }

    public AutoDiscoveryService(HttpClient httpClient, ILookupClient dnsClient, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _dnsClient = dnsClient ?? throw new ArgumentNullException(nameof(dnsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ServerConfiguration?> DiscoverAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            _logger.Warning("DiscoverAsync called with null or empty email address");
            return null;
        }

        var atIndex = emailAddress.IndexOf('@');
        if (atIndex < 1 || atIndex >= emailAddress.Length - 1)
        {
            _logger.Warning("Invalid email address format: {EmailAddress}", emailAddress);
            return null;
        }

        var domain = emailAddress[(atIndex + 1)..].Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(domain) || !domain.Contains('.'))
        {
            _logger.Warning("Invalid domain extracted from email: {Domain}", domain);
            return null;
        }

        _logger.Information("Starting AutoDiscovery for domain {Domain}", domain);

        // Levels 1-3: Try autoconfig for the email domain
        var config = await TryDiscoverForDomainAsync(domain, cancellationToken);
        if (config != null)
            return config;

        // Level 4: MX record lookup, then retry levels 1-3 with MX domain
        _logger.Information("Levels 1-3 failed for {Domain}, attempting MX record lookup", domain);
        var mxDomain = await ResolveMxDomainAsync(domain, cancellationToken);
        if (!string.IsNullOrEmpty(mxDomain) && !string.Equals(mxDomain, domain, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Information("MX record resolved to {MxDomain}, retrying discovery", mxDomain);
            config = await TryDiscoverForDomainAsync(mxDomain, cancellationToken);
            if (config != null)
                return config;
        }

        // Level 5: RFC 6186 SRV records (_imaps._tcp, _submission._tcp)
        _logger.Information("Levels 1-4 failed for {Domain}, trying RFC 6186 SRV records", domain);
        config = await TryDiscoverViaSrvAsync(domain, cancellationToken);
        if (config != null)
            return config;

        // Level 6: Return null
        _logger.Information("AutoDiscovery failed for {EmailAddress}: all levels exhausted", emailAddress);
        return null;
    }

    private async Task<ServerConfiguration?> TryDiscoverForDomainAsync(string domain, CancellationToken cancellationToken)
    {
        // Run Levels 1-3 in parallel (Thunderbird uses promiseFirstSuccessful)
        var urls = new[]
        {
            ($"https://autoconfig.{domain}/mail/config-v1.1.xml", "Level 1 (autoconfig subdomain HTTPS)"),
            ($"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml", "Level 2 (well-known HTTPS)"),
            ($"https://autoconfig.thunderbird.net/v1.1/{domain}", "Level 3 (Thunderbird ISPDB)"),
            // HTTP fallback for Levels 1-2 (many enterprise/small ISP servers only serve HTTP)
            ($"http://autoconfig.{domain}/mail/config-v1.1.xml", "Level 1 (autoconfig subdomain HTTP)"),
            ($"http://{domain}/.well-known/autoconfig/mail/config-v1.1.xml", "Level 2 (well-known HTTP)"),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = urls.Select(u => TryFetchAndParseAsync(u.Item1, u.Item2, cts.Token)).ToList();

        // Return the first successful result, cancel remaining requests
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            try
            {
                var config = await completed;
                if (config != null)
                {
                    // Cancel remaining parallel requests
                    await cts.CancelAsync();
                    return config;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Individual fetch failed, continue waiting for others
            }
        }

        return null;
    }

    private async Task<ServerConfiguration?> TryFetchAndParseAsync(string url, string levelDescription, CancellationToken cancellationToken)
    {
        _logger.Debug("Trying {Level}: {Url}", levelDescription, url);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            var response = await _httpClient.GetAsync(url, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Debug("{Level} returned HTTP {StatusCode}", levelDescription, (int)response.StatusCode);
                return null;
            }

            var xml = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var config = ParseAutoconfigXml(xml);

            if (config != null)
            {
                _logger.Information("AutoDiscovery succeeded via {Level}: IMAP={ImapHost}:{ImapPort}, SMTP={SmtpHost}:{SmtpPort}",
                    levelDescription, config.ImapHost, config.ImapPort, config.SmtpHost, config.SmtpPort);
            }
            else
            {
                _logger.Debug("{Level} returned XML but parsing yielded no valid configuration", levelDescription);
            }

            return config;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Respect caller cancellation
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "{Level} failed with exception", levelDescription);
            return null;
        }
    }

    internal static ServerConfiguration? ParseAutoconfigXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        var doc = XDocument.Parse(xml);
        var clientConfig = doc.Root;
        if (clientConfig == null)
            return null;

        var emailProvider = clientConfig.Element("emailProvider");
        if (emailProvider == null)
            return null;

        var incomingServer = emailProvider
            .Elements("incomingServer")
            .FirstOrDefault(e => string.Equals(e.Attribute("type")?.Value, "imap", StringComparison.OrdinalIgnoreCase));

        var outgoingServer = emailProvider
            .Elements("outgoingServer")
            .FirstOrDefault(e => string.Equals(e.Attribute("type")?.Value, "smtp", StringComparison.OrdinalIgnoreCase));

        if (incomingServer == null && outgoingServer == null)
            return null;

        var config = new ServerConfiguration();

        if (incomingServer != null)
        {
            config.ImapHost = incomingServer.Element("hostname")?.Value?.Trim() ?? string.Empty;

            if (int.TryParse(incomingServer.Element("port")?.Value?.Trim(), out var imapPort))
                config.ImapPort = imapPort;

            config.ImapEncryption = ParseSocketType(incomingServer.Element("socketType")?.Value);

            // Parse authentication method (e.g., "OAuth2", "password-cleartext", "password-encrypted")
            var auth = incomingServer.Element("authentication")?.Value?.Trim();
            if (!string.IsNullOrEmpty(auth))
                config.Authentication = auth;
        }

        if (outgoingServer != null)
        {
            config.SmtpHost = outgoingServer.Element("hostname")?.Value?.Trim() ?? string.Empty;

            if (int.TryParse(outgoingServer.Element("port")?.Value?.Trim(), out var smtpPort))
                config.SmtpPort = smtpPort;

            config.SmtpEncryption = ParseSocketType(outgoingServer.Element("socketType")?.Value);
        }

        // Require at least an IMAP host to consider the config valid
        if (string.IsNullOrEmpty(config.ImapHost))
            return null;

        return config;
    }

    internal static ConnectionEncryptionType ParseSocketType(string? socketType)
    {
        if (string.IsNullOrWhiteSpace(socketType))
            return ConnectionEncryptionType.None;

        return socketType.Trim().ToUpperInvariant() switch
        {
            "SSL" => ConnectionEncryptionType.Ssl,
            "STARTTLS" => ConnectionEncryptionType.StartTls,
            "PLAIN" => ConnectionEncryptionType.None,
            _ => ConnectionEncryptionType.None
        };
    }

    /// <summary>
    /// Resolves the MX record for a domain and extracts the base domain using DnsClient.NET.
    /// Protected virtual to allow mocking in tests.
    /// </summary>
    protected internal virtual async Task<string?> ResolveMxDomainAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            _logger.Debug("Resolving MX record for {Domain}", domain);

            var result = await _dnsClient.QueryAsync(domain, QueryType.MX, cancellationToken: cancellationToken);

            var mxRecord = result.Answers.MxRecords()
                .OrderBy(mx => mx.Preference)
                .FirstOrDefault();

            if (mxRecord == null)
            {
                _logger.Debug("No MX record found for {Domain}", domain);
                return null;
            }

            var mxHost = mxRecord.Exchange.Value.TrimEnd('.');
            _logger.Debug("MX record for {Domain}: {MxHost} (preference {Preference})",
                domain, mxHost, mxRecord.Preference);

            return ExtractBaseDomain(mxHost);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "MX record resolution failed for {Domain}", domain);
            return null;
        }
    }

    /// <summary>
    /// Attempts to discover server configuration via RFC 6186 SRV records.
    /// Queries _imaps._tcp.{domain} for IMAP and _submission._tcp.{domain} for SMTP.
    /// </summary>
    protected internal virtual async Task<ServerConfiguration?> TryDiscoverViaSrvAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            // Start SMTP query in parallel with IMAP queries (independent)
            var smtpTask = TrySrvQueryAsync($"_submission._tcp.{domain}", cancellationToken);

            // Query IMAP SRV: try _imaps._tcp (implicit TLS) first, then _imap._tcp (STARTTLS)
            var imapResult = await TrySrvQueryAsync($"_imaps._tcp.{domain}", cancellationToken);
            var imapEncryption = ConnectionEncryptionType.Ssl;

            if (imapResult == null)
            {
                imapResult = await TrySrvQueryAsync($"_imap._tcp.{domain}", cancellationToken);
                imapEncryption = ConnectionEncryptionType.StartTls;
            }

            if (imapResult == null)
            {
                _logger.Debug("No IMAP SRV records found for {Domain}", domain);
                return null;
            }

            var smtpResult = await smtpTask;

            var config = new ServerConfiguration
            {
                ImapHost = imapResult.Value.Host,
                ImapPort = imapResult.Value.Port,
                ImapEncryption = imapEncryption,
                SmtpHost = smtpResult?.Host ?? string.Empty,
                SmtpPort = smtpResult?.Port ?? 587,
                SmtpEncryption = smtpResult != null ? ConnectionEncryptionType.StartTls : ConnectionEncryptionType.None
            };

            _logger.Information("AutoDiscovery succeeded via SRV records: IMAP={ImapHost}:{ImapPort}, SMTP={SmtpHost}:{SmtpPort}",
                config.ImapHost, config.ImapPort, config.SmtpHost, config.SmtpPort);

            return config;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "SRV record discovery failed for {Domain}", domain);
            return null;
        }
    }

    private async Task<(string Host, int Port)?> TrySrvQueryAsync(string srvName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _dnsClient.QueryAsync(srvName, QueryType.SRV, cancellationToken: cancellationToken);

            var srvRecord = result.Answers.SrvRecords()
                .OrderBy(s => s.Priority)
                .ThenByDescending(s => s.Weight)
                .FirstOrDefault();

            if (srvRecord == null)
                return null;

            // RFC 6186 §3: a target of "." means the service is explicitly not available
            var host = srvRecord.Target.Value.TrimEnd('.');
            if (string.IsNullOrEmpty(host) || host == ".")
                return null;

            _logger.Debug("SRV {Name}: {Host}:{Port} (priority={Priority}, weight={Weight})",
                srvName, host, srvRecord.Port, srvRecord.Priority, srvRecord.Weight);

            return (host, srvRecord.Port);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the base domain from an MX hostname, handling ccSLDs.
    /// e.g., "alt1.gmail-smtp-in.l.google.com" → "google.com"
    /// e.g., "mx.mail.yahoo.co.uk" → "yahoo.co.uk"
    /// </summary>
    internal static string? ExtractBaseDomain(string mxHost)
    {
        if (string.IsNullOrWhiteSpace(mxHost))
            return null;

        var parts = mxHost.Split('.');
        if (parts.Length >= 3)
        {
            var twoLevelTld = $"{parts[^2]}.{parts[^1]}";
            if (TwoLevelTlds.Contains(twoLevelTld))
                return $"{parts[^3]}.{parts[^2]}.{parts[^1]}";
        }
        if (parts.Length >= 2)
        {
            return $"{parts[^2]}.{parts[^1]}";
        }

        return null;
    }
}
