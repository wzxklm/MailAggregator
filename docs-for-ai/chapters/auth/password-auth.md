# PasswordAuthService — Encrypt, store, and retrieve account passwords

## Overview

Thin service that bridges `Account` model objects and `ICredentialEncryptionService`. Encrypts a plain password and writes it to `Account.EncryptedPassword`, sets `Account.AuthType = Password`. Retrieves by decrypting the stored value. Does not persist to the database itself — callers must save the `Account` via `DbContext`.

## Key Behaviors

- **StorePassword**: Encrypts `plainPassword`, sets `account.EncryptedPassword` and `account.AuthType = AuthType.Password`
- **RetrievePassword**: Decrypts `account.EncryptedPassword`; throws `InvalidOperationException` if no password stored
- **HasStoredPassword**: Returns `true` if `account.EncryptedPassword` is non-null/non-empty
- **ClearPassword**: Sets `account.EncryptedPassword = null`

## Interface

`IPasswordAuthService` — `StorePassword(Account, string)`, `RetrievePassword(Account)`, `HasStoredPassword(Account)`, `ClearPassword(Account)`

## Internal Details

Constructor: `PasswordAuthService(ICredentialEncryptionService encryptionService, ILogger logger)`

All methods validate arguments with `ArgumentNullException.ThrowIfNull`. Logging uses `account.EmailAddress` for traceability.

## Dependencies

- Uses: `ICredentialEncryptionService`
- Used by: `AccountService`
