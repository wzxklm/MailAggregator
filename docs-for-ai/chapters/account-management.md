# Account Management — full lifecycle management of email accounts

## Files

| File | Responsibility |
|------|----------------|
| `src/MailAggregator.Core/Services/AccountManagement/IAccountService.cs` | Interface defining account CRUD + validation contract |
| `src/MailAggregator.Core/Services/AccountManagement/AccountService.cs` | Implementation: add, update, delete, retrieve, validate accounts |

## AccountService

### Overview

Orchestrates the complete lifecycle of email accounts: adding (with auto-discovery or manual config), updating (with live sync restart), deleting (with full resource cleanup), retrieval, and IMAP connection validation. Acts as the central coordination point between discovery, auth, sync, and persistence layers.

### Key Behaviors

- **Add account (multi-step)**: Checks for duplicates → resolves server config (manual or auto-discovery) → creates `Account` entity → determines auth type (explicit, or auto-detect OAuth via `FindProviderByHost`) → stores password or prepares OAuth → validates IMAP connection (password-auth only; OAuth has no tokens yet) → saves in DB transaction
- **Auth type auto-detection**: If caller doesn't specify `authType`, checks whether the IMAP host matches a known OAuth provider; falls back to `AuthType.Password` otherwise
- **Update with sync restart**: Validates server settings (host non-empty, port 1-65535) → detaches stale tracked entity to avoid EF Core conflicts → saves → if account was actively syncing, stops sync, evicts pooled connections, restarts sync with new config
- **Delete with full cleanup**: Stops background sync → removes pooled IMAP connections → removes token refresh lock → deletes downloaded attachment files from disk → re-fetches fresh entity (avoids concurrency exception from stale tracking) → removes account (cascade delete handles DB children)
- **Retrieval**: `GetAllAccountsAsync` and `GetAccountByIdAsync` both use `AsNoTracking()` to avoid EF tracking conflicts with long-lived root-scoped DbContext
- **Connection validation**: Opens a temporary IMAP connection via `IImapConnectionService`, returns `true`/`false` — used during add flow and available for on-demand checks
- **Transaction safety**: `AddAccountAsync` wraps the DB save in an explicit transaction with rollback on failure
- **EF tracking workaround**: Both `UpdateAccountAsync` and `DeleteAccountAsync` manually detach previously-tracked entities before operating, preventing `InvalidOperationException` from the long-lived root-scoped DbContext

### Interface

`IAccountService` — `AddAccountAsync(emailAddress, password?, manualConfig?, authType?)`, `UpdateAccountAsync(account)`, `DeleteAccountAsync(accountId)`, `GetAllAccountsAsync()`, `GetAccountByIdAsync(accountId)`, `ValidateConnectionAsync(account)`

### Internal Details

- Delete cleanup order matters: sync must stop before connection pool eviction; attachment files are deleted individually with per-file error swallowing (logged as warning) so one bad path doesn't block account removal
- `DeleteAccountAsync` calls `MailConnectionHelper.RemoveTokenRefreshLock(accountId)` to clean up the static semaphore used during OAuth token refresh — prevents memory leaks for deleted accounts
- `UpdateAccountAsync` detaches stale tracked entities by scanning `ChangeTracker.Entries<Account>()` for matching ID, then calls `_dbContext.Accounts.Update(account)` which re-attaches as Modified

### Dependencies

- Uses: `MailAggregatorDbContext`, `IAutoDiscoveryService`, `IOAuthService`, `IPasswordAuthService`, `IImapConnectionService`, `ISyncManager`, `IImapConnectionPool`, `MailConnectionHelper` (static)
- Used by: `MainViewModel`, `AccountListViewModel`, `AddAccountViewModel`
