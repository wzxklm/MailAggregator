using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailAggregator.Core.Models;
using Serilog;

namespace MailAggregator.Core.Services.Auth;

/// <summary>
/// Implements OAuth 2.0 Authorization Code flow with PKCE (Proof Key for Code Exchange).
/// Manages provider configuration, authorization URL generation, local callback listener,
/// token exchange, and token refresh.
/// </summary>
public class OAuthService : IOAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ICredentialEncryptionService _encryptionService;
    private readonly ILogger _logger;
    private readonly List<OAuthProviderConfig> _providers;

    public OAuthService(
        HttpClient httpClient,
        ICredentialEncryptionService encryptionService,
        ILogger logger,
        string providersJsonPath)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _providers = LoadProviders(providersJsonPath);
        _logger.Information("Loaded {Count} OAuth providers from {Path}", _providers.Count, providersJsonPath);
    }

    /// <inheritdoc />
    public OAuthProviderConfig? FindProviderByHost(string serverHost)
    {
        if (string.IsNullOrWhiteSpace(serverHost))
            return null;

        return _providers.FirstOrDefault(p =>
            p.ServerHosts.Any(h => string.Equals(h, serverHost, StringComparison.OrdinalIgnoreCase)));
    }

    /// <inheritdoc />
    public (string authorizationUrl, string codeVerifier, int listenerPort) PrepareAuthorization(OAuthProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var listenerPort = FindFreePort();
        var redirectUri = $"http://localhost:{listenerPort}/";

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["scope"] = string.Join(" ", provider.Scopes)
        };

        var queryString = string.Join("&", queryParams.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        var authorizationUrl = $"{provider.AuthorizationEndpoint}?{queryString}";

        _logger.Information("Prepared OAuth authorization for {Provider} on port {Port}", provider.Name, listenerPort);

        return (authorizationUrl, codeVerifier, listenerPort);
    }

    /// <inheritdoc />
    public async Task<string> WaitForAuthorizationCodeAsync(int listenerPort, CancellationToken cancellationToken = default)
    {
        var prefix = $"http://localhost:{listenerPort}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        _logger.Information("Listening for OAuth callback on {Prefix}", prefix);

        try
        {
            // Use cancellation token to allow aborting the wait
            using var registration = cancellationToken.Register(() => listener.Stop());

            var context = await listener.GetContextAsync().ConfigureAwait(false);
            var code = context.Request.QueryString["code"];

            if (string.IsNullOrEmpty(code))
            {
                var error = context.Request.QueryString["error"] ?? "unknown";
                var errorDescription = context.Request.QueryString["error_description"] ?? "No authorization code received.";
                _logger.Error("OAuth callback error: {Error} - {Description}", error, errorDescription);

                await SendResponseAsync(context.Response, "Authorization failed. You can close this window.").ConfigureAwait(false);
                throw new InvalidOperationException($"OAuth authorization failed: {error} - {errorDescription}");
            }

            await SendResponseAsync(context.Response, "Authorization successful, you can close this window.").ConfigureAwait(false);

            _logger.Information("Received OAuth authorization code");
            return code;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResult> ExchangeCodeForTokenAsync(
        OAuthProviderConfig provider,
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var requestBody = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = provider.ClientId,
            ["code_verifier"] = codeVerifier
        };

        return await PostTokenRequestAsync(provider, requestBody, "Token exchange", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResult> RefreshTokenAsync(
        OAuthProviderConfig provider,
        string encryptedRefreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var refreshToken = _encryptionService.Decrypt(encryptedRefreshToken);

        var requestBody = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = provider.ClientId
        };

        return await PostTokenRequestAsync(provider, requestBody, "Token refresh", cancellationToken);
    }

    private async Task<OAuthTokenResult> PostTokenRequestAsync(
        OAuthProviderConfig provider,
        Dictionary<string, string> requestBody,
        string operationName,
        CancellationToken cancellationToken)
    {
        _logger.Information("{Operation} for {Provider}", operationName, provider.Name);

        using var content = new FormUrlEncodedContent(requestBody);
        using var response = await _httpClient.PostAsync(provider.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.Error("{Operation} failed with status {StatusCode}: {Body}", operationName, response.StatusCode, responseBody);
            throw new HttpRequestException($"{operationName} failed with status {response.StatusCode}: {responseBody}");
        }

        return ParseTokenResponse(responseBody);
    }

    /// <summary>
    /// Generates a cryptographically random code_verifier for PKCE.
    /// Length: 43-128 characters from the unreserved character set [A-Za-z0-9-._~].
    /// </summary>
    internal static string GenerateCodeVerifier()
    {
        const string allowedChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        const int length = 128;

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = allowedChars[RandomNumberGenerator.GetInt32(allowedChars.Length)];
        }

        return new string(chars);
    }

    /// <summary>
    /// Computes the PKCE code_challenge from the code_verifier: Base64Url(SHA256(code_verifier)).
    /// </summary>
    internal static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private OAuthTokenResult ParseTokenResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Token response missing access_token");

        var refreshToken = root.TryGetProperty("refresh_token", out var refreshElement)
            ? refreshElement.GetString()
            : null;

        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement)
            ? expiresElement.GetInt32()
            : 3600; // Default to 1 hour

        var result = new OAuthTokenResult
        {
            AccessToken = _encryptionService.Encrypt(accessToken),
            RefreshToken = refreshToken != null ? _encryptionService.Encrypt(refreshToken) : null,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        };

        _logger.Information("Token response parsed successfully, expires at {ExpiresAt}", result.ExpiresAt);
        return result;
    }

    private static List<OAuthProviderConfig> LoadProviders(string providersJsonPath)
    {
        string json;
        try
        {
            json = File.ReadAllText(providersJsonPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"OAuth providers configuration file not found: {providersJsonPath}", providersJsonPath, ex);
        }

        return JsonSerializer.Deserialize<List<OAuthProviderConfig>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize OAuth providers configuration.");
    }

    private static async Task SendResponseAsync(HttpListenerResponse response, string message)
    {
        var encodedMessage = WebUtility.HtmlEncode(message);
        var html = $"""
            <!DOCTYPE html>
            <html>
            <head><title>OAuth Authorization</title></head>
            <body><h1>{encodedMessage}</h1></body>
            </html>
            """;

        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.StatusCode = 200;

        await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
        response.Close();
    }
}
