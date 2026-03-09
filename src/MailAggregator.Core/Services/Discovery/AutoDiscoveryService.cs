using System.Text.RegularExpressions;
using System.Xml.Linq;
using MailAggregator.Core.Models;
using Serilog;

namespace MailAggregator.Core.Services.Discovery;

/// <summary>
/// Discovers IMAP/SMTP server configuration using a 5-level fallback:
/// 1. autoconfig.{domain}/mail/config-v1.1.xml
/// 2. {domain}/.well-known/autoconfig/mail/config-v1.1.xml
/// 3. Thunderbird ISPDB (autoconfig.thunderbird.net)
/// 4. MX record DNS lookup, then retry levels 1-3 with MX domain
/// 5. Return null (UI will guide manual config)
/// </summary>
public class AutoDiscoveryService : IAutoDiscoveryService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NslookupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Regex for validating domain names (only alphanumeric, hyphens, and dots).
    /// Prevents command injection when passing domains to nslookup.
    /// </summary>
    private static readonly Regex ValidDomainRegex = new(
        @"^[a-zA-Z0-9]([a-zA-Z0-9\-\.]*[a-zA-Z0-9])?$",
        RegexOptions.Compiled);

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
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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

        // Level 5: Return null
        _logger.Information("AutoDiscovery failed for {EmailAddress}: all levels exhausted", emailAddress);
        return null;
    }

    private async Task<ServerConfiguration?> TryDiscoverForDomainAsync(string domain, CancellationToken cancellationToken)
    {
        // Level 1: autoconfig.{domain}
        var url1 = $"https://autoconfig.{domain}/mail/config-v1.1.xml";
        var config = await TryFetchAndParseAsync(url1, "Level 1 (autoconfig subdomain)", cancellationToken);
        if (config != null)
            return config;

        // Level 2: {domain}/.well-known/autoconfig
        var url2 = $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml";
        config = await TryFetchAndParseAsync(url2, "Level 2 (well-known path)", cancellationToken);
        if (config != null)
            return config;

        // Level 3: Thunderbird ISPDB
        var url3 = $"https://autoconfig.thunderbird.net/v1.1/{domain}";
        config = await TryFetchAndParseAsync(url3, "Level 3 (Thunderbird ISPDB)", cancellationToken);
        if (config != null)
            return config;

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
    /// Resolves the MX record for a domain and extracts the base domain.
    /// Protected virtual to allow mocking in tests.
    /// </summary>
    protected internal virtual async Task<string?> ResolveMxDomainAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            // Validate domain to prevent command injection
            if (!ValidDomainRegex.IsMatch(domain))
            {
                _logger.Warning("Invalid domain name, skipping MX lookup: {Domain}", domain);
                return null;
            }

            _logger.Debug("Resolving MX record for {Domain}", domain);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nslookup",
                Arguments = $"-type=MX {domain}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                _logger.Warning("Failed to start nslookup process");
                return null;
            }

            // Use a combined timeout + caller cancellation token
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(NslookupTimeout);

            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                return ParseMxFromNslookup(output);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout (not caller cancellation) — kill the hung process
                _logger.Warning("MX lookup timed out for {Domain}, killing nslookup process", domain);
                try { process.Kill(); } catch { /* best effort */ }
                return null;
            }
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

    internal static string? ParseMxFromNslookup(string nslookupOutput)
    {
        if (string.IsNullOrWhiteSpace(nslookupOutput))
            return null;

        // nslookup output lines like: "gmail.com	mail exchanger = 10 alt1.gmail-smtp-in.l.google.com."
        // or "gmail.com	MX preference = 10, mail exchanger = alt1.gmail-smtp-in.l.google.com"
        foreach (var line in nslookupOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            var exchangerIdx = trimmed.IndexOf("mail exchanger", StringComparison.OrdinalIgnoreCase);
            if (exchangerIdx < 0)
                continue;

            // Find the '=' after "mail exchanger"
            var equalsIdx = trimmed.IndexOf('=', exchangerIdx);
            if (equalsIdx < 0)
                continue;

            var afterEquals = trimmed[(equalsIdx + 1)..].Trim();

            // The MX host might be preceded by a priority number (e.g., "10 mail.example.com.")
            var parts = afterEquals.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var mxHost = parts.Length switch
            {
                0 => null,
                1 => parts[0],
                _ => parts[^1] // Last part is the hostname
            };

            if (string.IsNullOrWhiteSpace(mxHost))
                continue;

            // Remove trailing dot
            mxHost = mxHost.TrimEnd('.');

            // Extract base domain from MX hostname (e.g., "alt1.gmail-smtp-in.l.google.com" → "google.com")
            // Handle country-code second-level domains (e.g., "mx.mail.yahoo.co.uk" → "yahoo.co.uk")
            var mxParts = mxHost.Split('.');
            if (mxParts.Length >= 3)
            {
                var twoLevelTld = $"{mxParts[^2]}.{mxParts[^1]}";
                if (TwoLevelTlds.Contains(twoLevelTld))
                    return $"{mxParts[^3]}.{mxParts[^2]}.{mxParts[^1]}";
            }
            if (mxParts.Length >= 2)
            {
                return $"{mxParts[^2]}.{mxParts[^1]}";
            }
        }

        return null;
    }
}
