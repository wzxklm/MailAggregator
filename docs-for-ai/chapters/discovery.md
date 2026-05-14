# Discovery — IMAP/SMTP server auto-configuration via multi-level fallback

## Files

| File | Responsibility |
|------|----------------|
| `src/MailAggregator.Core/Services/Discovery/IAutoDiscoveryService.cs` | Interface: single `DiscoverAsync` method |
| `src/MailAggregator.Core/Services/Discovery/AutoDiscoveryService.cs` | Implementation: 6-level fallback discovery |

## AutoDiscoveryService

### Overview

Discovers IMAP/SMTP `ServerConfiguration` for a given email address by trying progressively broader strategies. Modeled after Thunderbird's autoconfig algorithm. Returns `null` when all levels fail, signaling the UI to prompt for manual configuration.

### Key Behaviors

- **6-level fallback chain**: (1) `autoconfig.{domain}` subdomain, (2) `{domain}/.well-known/autoconfig`, (3) Thunderbird ISPDB (`autoconfig.thunderbird.net`), (4) MX DNS lookup then retry 1-3 with MX base domain, (5) RFC 6186 SRV records (`_imaps._tcp`, `_submission._tcp`), (6) return `null`
- **Parallel fetch (levels 1-3)**: All autoconfig URLs (HTTPS + HTTP fallbacks, 5 total) fire concurrently; first success cancels the rest via linked `CancellationTokenSource`
- **Per-request timeout**: Each HTTP fetch has a 10s timeout (`RequestTimeout`); DNS queries also use 10s (`DnsTimeout`)
- **ccSLD-aware MX extraction**: `ExtractBaseDomain` handles two-level TLDs (e.g. `co.uk`, `com.au`) via `TwoLevelTlds` hashset so MX host `mx.mail.yahoo.co.uk` yields `yahoo.co.uk`, not `co.uk`
- **SRV record priority/weight**: SRV results ordered by priority ascending, then weight descending per RFC 2782
- **SRV encryption inference**: `_imaps._tcp` implies SSL; fallback `_imap._tcp` implies STARTTLS; `_submission._tcp` assumes STARTTLS
- **Graceful failure**: Every level catches exceptions individually and returns `null`; only caller cancellation (`CancellationToken`) propagates

### Interface

`IAutoDiscoveryService` — `DiscoverAsync(string emailAddress, CancellationToken)`

Returns `Task<ServerConfiguration?>`. `null` means discovery exhausted all levels.

### Internal Details

| Method | Visibility | Purpose |
|--------|-----------|---------|
| `TryDiscoverForDomainAsync` | private | Runs levels 1-3 in parallel for a given domain |
| `TryFetchAndParseAsync` | private | Fetches one autoconfig URL, parses XML |
| `ParseAutoconfigXml` | internal static | Parses Thunderbird-format autoconfig XML into `ServerConfiguration` |
| `ParseSocketType` | internal static | Maps XML `socketType` ("SSL"/"STARTTLS"/"PLAIN") to `ConnectionEncryptionType` |
| `ResolveMxDomainAsync` | protected internal virtual | MX lookup via `ILookupClient`, extracts base domain (mockable in tests) |
| `TryDiscoverViaSrvAsync` | protected internal virtual | RFC 6186 SRV lookup for IMAP + SMTP (mockable in tests) |
| `TrySrvQueryAsync` | private | Single SRV query, returns `(Host, Port)?`; respects RFC 6186 "." target = not available |
| `ExtractBaseDomain` | internal static | Strips MX hostname to registrable domain, ccSLD-aware |

**Autoconfig XML parsing**: Expects `<clientConfig><emailProvider>` with `<incomingServer type="imap">` and `<outgoingServer type="smtp">`. Extracts `hostname`, `port`, `socketType`, `authentication`. Requires at least a valid IMAP host to return non-null.

**Constructor overloads**: Default constructor creates `LookupClient` with 10s timeout; second constructor accepts `ILookupClient` for test injection.

### Dependencies

- Uses: `HttpClient`, `DnsClient.ILookupClient`, `Serilog.ILogger`, `ServerConfiguration` model, `ConnectionEncryptionType` enum
- Used by: `AccountService`, `AddAccountViewModel`
