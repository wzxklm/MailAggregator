# ImapConnectionService — Connect and authenticate IMAP clients

## Overview

Creates authenticated `ImapClient` instances for a given `Account`. Handles XOAUTH2 and password auth, SOCKS5 proxy configuration, IMAP ID command (RFC 2971), and retry with exponential backoff. Delegates shared auth/proxy/encryption logic to `MailConnectionHelper`. Caller owns the returned client and must dispose it.

## Key Behaviors

- **Retry with backoff**: Up to `MaxRetries` (3) attempts with exponential delay (1s, 2s, 4s). Non-transient auth errors (`IsNonTransientAuthError`) skip retries immediately.
- **IMAP ID (RFC 2971)**: Sends `IDENTIFY` before auth if server advertises `Id` capability. Required by providers like 163.com (Coremail) that reject `LOGIN` without prior client identification. Failure is non-fatal.
- **OAuth token persistence**: On token refresh during auth, persists updated tokens to DB via `PersistRefreshedTokenAsync` callback passed to `MailConnectionHelper.AuthenticateAsync`.
- **Cancellation-safe**: `OperationCanceledException` disposes the client and re-throws without retry.

## Interface

`IImapConnectionService` — `ConnectAsync(Account, CancellationToken)`

Returns a connected, authenticated `ImapClient`. Caller is responsible for disposing.

## Internal Details

Constructor dependencies: `ICredentialEncryptionService`, `IOAuthService`, `IDbContextFactory<MailAggregatorDbContext>`, `ILogger`.

`PersistRefreshedTokenAsync` creates a scoped `DbContext`, calls `Update(account)` + `SaveChangesAsync`. Swallows exceptions to avoid failing the connection because of a persistence error.

## Dependencies

- Uses: `MailConnectionHelper` (auth, proxy, retry constants), `ICredentialEncryptionService`, `IOAuthService`, `IDbContextFactory<MailAggregatorDbContext>`
- Used by: `ImapConnectionPool`, `SyncManager`, `EmailSendService` (Sent folder append)
