# SmtpConnectionService — Connect and authenticate SMTP clients

## Overview

Creates authenticated `SmtpClient` instances for a given `Account`. Mirrors `ImapConnectionService` but for SMTP: same retry logic, proxy support, and OAuth/password auth via `MailConnectionHelper`. Caller owns the returned client and must dispose it.

## Key Behaviors

- **Retry with backoff**: Up to `MaxRetries` (3) attempts with exponential delay. Unlike IMAP, does not have a special non-transient auth error check — all exceptions trigger retry.
- **OAuth token persistence**: Same `PersistRefreshedTokenAsync` callback pattern as IMAP — persists refreshed tokens to DB after `MailConnectionHelper.AuthenticateAsync` refreshes them.
- **SOCKS5 proxy**: Configured via `MailConnectionHelper.ConfigureProxy` using `Account.ProxyHost`/`ProxyPort`.

## Interface

`ISmtpConnectionService` — `ConnectAsync(Account, CancellationToken)`

Returns a connected, authenticated `SmtpClient`. Caller is responsible for disposing.

## Internal Details

Constructor dependencies: `ICredentialEncryptionService`, `IOAuthService`, `IDbContextFactory<MailAggregatorDbContext>`, `ILogger`.

Structurally identical to `ImapConnectionService` except: uses `SmtpClient`, reads `SmtpHost`/`SmtpPort`/`SmtpEncryption` from `Account`, and does not send an ID command.

## Dependencies

- Uses: `MailConnectionHelper` (auth, proxy, retry constants), `ICredentialEncryptionService`, `IOAuthService`, `IDbContextFactory<MailAggregatorDbContext>`
- Used by: `EmailSendService`
