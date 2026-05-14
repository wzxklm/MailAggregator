# AI — Email translation and summarization via OpenAI-compatible API

## Files

| File | Responsibility |
|------|----------------|
| `src/MailAggregator.Core/Services/Ai/IAiSettingsService.cs` | AI settings interface (CRUD + decryption + default prompts) |
| `src/MailAggregator.Core/Services/Ai/AiSettingsService.cs` | Loads/saves singleton `AiSettings` row; encrypts API key via `CredentialEncryptionService` |
| `src/MailAggregator.Core/Services/Ai/IAiService.cs` | Translate/summarize interface taking an `EmailMessage` |
| `src/MailAggregator.Core/Services/Ai/AiService.cs` | OpenAI-compatible HTTP client (`POST {baseUrl}/chat/completions`, non-streaming) |

## AiSettingsService

### Overview

Loads and persists the singleton `AiSettings` row (Id=1). Encrypts the API key via `CredentialEncryptionService` before storage and decrypts on demand. Exposes default prompts so callers can pre-fill the settings dialog.

### Key Behaviors

- **Singleton row**: All settings stored in row with `Id = 1`. `GetAsync` returns an in-memory default if no row exists yet (not added to DB until `SaveAsync` is called)
- **API key encryption**: Reuses `CredentialEncryptionService` (AES-256-GCM). Empty plaintext is stored as empty string — `Encrypt` is not called for an empty key
- **Default prompts**: `{language}` placeholder is replaced at request time by `AiService`. Two built-in prompt templates (translate / summarize) returned by `GetDefaultTranslatePrompt` / `GetDefaultSummarizePrompt`
- **AsNoTracking reads**: `GetAsync` uses `.AsNoTracking()`. `SaveAsync` does a tracked lookup and `Add` or property writes accordingly

### Interface

`IAiSettingsService` — `GetAsync(ct)`, `SaveAsync(settings, apiKeyPlaintext, ct)`, `GetDecryptedApiKey(settings)`, `GetDefaultTranslatePrompt()`, `GetDefaultSummarizePrompt()`

### Dependencies

- Uses: `IDbContextFactory<MailAggregatorDbContext>`, `ICredentialEncryptionService`
- Used by: `AiService`, `AiSettingsViewModel`

---

## AiService

### Overview

Calls an OpenAI-compatible Chat Completions endpoint to translate or summarize a single email. Builds the request from `EmailMessage` (subject, from/to, body) and the user's stored system prompt with `{language}` substitution. Returns the raw markdown string from the model's response.

### Key Behaviors

- **Endpoint construction**: `{BaseUrl.TrimEnd('/')}/chat/completions` — if the user already includes the path, it is left as-is
- **Authorization**: Sends `Bearer {apiKey}` header when an API key is configured; omitted when the key is empty (useful for local/self-hosted models)
- **Non-streaming**: `stream: false` in the JSON body. Full response is read before parsing
- **Email content build**: Adds `Subject:`, `From:`, `To:` lines then the body. Uses `BodyText` if present, otherwise strips HTML tags from `BodyHtml` via regex (script/style blocks removed first, then tags, then HTML-decoded and whitespace-collapsed)
- **Configuration validation**: Throws `InvalidOperationException` with a user-facing message if `BaseUrl` or `Model` is missing
- **Error handling**: Non-2xx responses throw `HttpRequestException` with status + truncated body (500 chars). Empty model response throws `InvalidOperationException`
- **Cancellation**: `CancellationToken` propagated to `HttpClient.SendAsync` and content read

### Interface

`IAiService` — `TranslateAsync(message, languageOverride?, ct)`, `SummarizeAsync(message, languageOverride?, ct)`

### Internal Details

Request/response DTOs (`ChatRequest`, `ChatMessage`, `ChatResponse`, `ChatChoice`) are nested private classes using `System.Text.Json` with `JsonPropertyName` attributes. `JsonSerializerOptions` uses `Web` defaults with `WhenWritingNull` ignore condition. `StripHtml` is a single-pass regex strip — adequate for AI input (token reduction); it is NOT used for rendering.

### Dependencies

- Uses: `HttpClient` (singleton), `IAiSettingsService`, `Serilog.ILogger`
- Used by: `MainViewModel` (via `App.Services.GetRequiredService<IAiService>()`)
