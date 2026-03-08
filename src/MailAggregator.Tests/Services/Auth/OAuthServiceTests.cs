using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using Moq;
using Moq.Protected;
using Serilog;

namespace MailAggregator.Tests.Services.Auth;

public class OAuthServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _providersJsonPath;
    private readonly Mock<ICredentialEncryptionService> _mockEncryption;
    private readonly Mock<ILogger> _mockLogger;

    private static readonly List<OAuthProviderConfig> TestProviders = new()
    {
        new OAuthProviderConfig
        {
            Name = "Gmail",
            ServerHosts = new List<string> { "imap.gmail.com", "smtp.gmail.com" },
            ClientId = "test-gmail-client-id",
            AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
            TokenEndpoint = "https://oauth2.googleapis.com/token",
            Scopes = new List<string> { "https://mail.google.com/" }
        },
        new OAuthProviderConfig
        {
            Name = "Microsoft",
            ServerHosts = new List<string> { "outlook.office365.com", "smtp.office365.com" },
            ClientId = "test-ms-client-id",
            AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            Scopes = new List<string> { "https://outlook.office365.com/IMAP.AccessAsUser.All", "offline_access" }
        }
    };

    public OAuthServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"oauth_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _providersJsonPath = Path.Combine(_tempDir, "oauth-providers.json");
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(_providersJsonPath, JsonSerializer.Serialize(TestProviders, jsonOptions));

        _mockEncryption = new Mock<ICredentialEncryptionService>();
        _mockEncryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns<string>(s => $"ENC:{s}");
        _mockEncryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns<string>(s => s.Replace("ENC:", ""));

        _mockLogger = new Mock<ILogger>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private OAuthService CreateService(HttpClient? httpClient = null)
    {
        return new OAuthService(
            httpClient ?? new HttpClient(),
            _mockEncryption.Object,
            _mockLogger.Object,
            _providersJsonPath);
    }

    #region FindProviderByHost

    [Theory]
    [InlineData("imap.gmail.com", "Gmail")]
    [InlineData("smtp.gmail.com", "Gmail")]
    [InlineData("outlook.office365.com", "Microsoft")]
    [InlineData("smtp.office365.com", "Microsoft")]
    public void FindProviderByHost_KnownHost_ReturnsCorrectProvider(string host, string expectedName)
    {
        var service = CreateService();

        var provider = service.FindProviderByHost(host);

        provider.Should().NotBeNull();
        provider!.Name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("IMAP.GMAIL.COM", "Gmail")]
    [InlineData("Imap.Gmail.Com", "Gmail")]
    [InlineData("OUTLOOK.OFFICE365.COM", "Microsoft")]
    public void FindProviderByHost_CaseInsensitive_ReturnsCorrectProvider(string host, string expectedName)
    {
        var service = CreateService();

        var provider = service.FindProviderByHost(host);

        provider.Should().NotBeNull();
        provider!.Name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("imap.unknown.com")]
    [InlineData("smtp.example.org")]
    [InlineData("")]
    [InlineData(null)]
    public void FindProviderByHost_UnknownHost_ReturnsNull(string? host)
    {
        var service = CreateService();

        var provider = service.FindProviderByHost(host!);

        provider.Should().BeNull();
    }

    #endregion

    #region PrepareAuthorization

    [Fact]
    public void PrepareAuthorization_GeneratesValidCodeVerifier()
    {
        var service = CreateService();
        var provider = TestProviders[0];

        var (_, codeVerifier, _) = service.PrepareAuthorization(provider);

        // RFC 7636: code_verifier must be 43-128 characters from [A-Za-z0-9-._~]
        codeVerifier.Length.Should().BeInRange(43, 128);
        codeVerifier.Should().MatchRegex(@"^[A-Za-z0-9\-._~]+$");
    }

    [Fact]
    public void PrepareAuthorization_GeneratesUniqueCodeVerifiers()
    {
        var service = CreateService();
        var provider = TestProviders[0];

        var (_, verifier1, _) = service.PrepareAuthorization(provider);
        var (_, verifier2, _) = service.PrepareAuthorization(provider);

        verifier1.Should().NotBe(verifier2, "each invocation should produce a unique code verifier");
    }

    [Fact]
    public void PrepareAuthorization_GeneratesValidAuthorizationUrl()
    {
        var service = CreateService();
        var provider = TestProviders[0];

        var (authUrl, _, listenerPort) = service.PrepareAuthorization(provider);

        var uri = new Uri(authUrl);
        uri.Scheme.Should().Be("https");
        uri.Host.Should().Be("accounts.google.com");

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["client_id"].Should().Be("test-gmail-client-id");
        query["response_type"].Should().Be("code");
        query["redirect_uri"].Should().Be($"http://localhost:{listenerPort}/");
        query["code_challenge"].Should().NotBeNullOrEmpty();
        query["code_challenge_method"].Should().Be("S256");
        query["scope"].Should().Be("https://mail.google.com/");
    }

    [Fact]
    public void PrepareAuthorization_ReturnsValidPort()
    {
        var service = CreateService();
        var provider = TestProviders[0];

        var (_, _, listenerPort) = service.PrepareAuthorization(provider);

        listenerPort.Should().BeInRange(1, 65535);
    }

    [Fact]
    public void PrepareAuthorization_MultipleScopes_JoinedWithSpace()
    {
        var service = CreateService();
        var provider = TestProviders[1]; // Microsoft has multiple scopes

        var (authUrl, _, _) = service.PrepareAuthorization(provider);

        var uri = new Uri(authUrl);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["scope"].Should().Contain("https://outlook.office365.com/IMAP.AccessAsUser.All");
        query["scope"].Should().Contain("offline_access");
    }

    #endregion

    #region ExchangeCodeForTokenAsync

    [Fact]
    public async Task ExchangeCodeForTokenAsync_ValidResponse_ReturnsEncryptedTokens()
    {
        var tokenResponse = new
        {
            access_token = "test-access-token",
            refresh_token = "test-refresh-token",
            expires_in = 3600
        };

        var mockHandler = CreateMockHttpHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(tokenResponse));

        var httpClient = new HttpClient(mockHandler.Object);
        var service = CreateService(httpClient);
        var provider = TestProviders[0];

        var result = await service.ExchangeCodeForTokenAsync(
            provider, "auth-code", "code-verifier", "http://localhost:12345/");

        result.Should().NotBeNull();
        result.AccessToken.Should().Be("ENC:test-access-token");
        result.RefreshToken.Should().Be("ENC:test-refresh-token");
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(3600), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExchangeCodeForTokenAsync_SendsCorrectParameters()
    {
        string? capturedContent = null;
        string? capturedUri = null;

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedUri = req.RequestUri?.ToString();
                capturedContent = await req.Content!.ReadAsStringAsync();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = "at", expires_in = 3600 }),
                    Encoding.UTF8,
                    "application/json")
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = CreateService(httpClient);
        var provider = TestProviders[0];

        await service.ExchangeCodeForTokenAsync(
            provider, "my-auth-code", "my-verifier", "http://localhost:9999/");

        capturedUri.Should().Be("https://oauth2.googleapis.com/token");
        capturedContent.Should().Contain("grant_type=authorization_code");
        capturedContent.Should().Contain("code=my-auth-code");
        capturedContent.Should().Contain("code_verifier=my-verifier");
        capturedContent.Should().Contain($"client_id={Uri.EscapeDataString("test-gmail-client-id")}");
        capturedContent.Should().Contain($"redirect_uri={Uri.EscapeDataString("http://localhost:9999/")}");
    }

    [Fact]
    public async Task ExchangeCodeForTokenAsync_NoRefreshToken_ReturnsNullRefreshToken()
    {
        var tokenResponse = new
        {
            access_token = "test-access-token",
            expires_in = 3600
        };

        var mockHandler = CreateMockHttpHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(tokenResponse));

        var httpClient = new HttpClient(mockHandler.Object);
        var service = CreateService(httpClient);
        var provider = TestProviders[0];

        var result = await service.ExchangeCodeForTokenAsync(
            provider, "auth-code", "code-verifier", "http://localhost:12345/");

        result.RefreshToken.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeCodeForTokenAsync_ErrorResponse_ThrowsHttpRequestException()
    {
        var mockHandler = CreateMockHttpHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\"}");

        var httpClient = new HttpClient(mockHandler.Object);
        var service = CreateService(httpClient);
        var provider = TestProviders[0];

        var act = () => service.ExchangeCodeForTokenAsync(
            provider, "bad-code", "verifier", "http://localhost:12345/");

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*BadRequest*");
    }

    #endregion

    #region RefreshTokenAsync

    [Fact]
    public async Task RefreshTokenAsync_ValidResponse_ReturnsNewEncryptedTokens()
    {
        var tokenResponse = new
        {
            access_token = "new-access-token",
            refresh_token = "new-refresh-token",
            expires_in = 7200
        };

        var mockHandler = CreateMockHttpHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(tokenResponse));

        var httpClient = new HttpClient(mockHandler.Object);
        var service = CreateService(httpClient);
        var provider = TestProviders[0];

        var result = await service.RefreshTokenAsync(provider, "ENC:old-refresh-token");

        result.Should().NotBeNull();
        result.AccessToken.Should().Be("ENC:new-access-token");
        result.RefreshToken.Should().Be("ENC:new-refresh-token");
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(7200), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RefreshTokenAsync_DecryptsRefreshTokenBeforeSending()
    {
        string? capturedContent = null;

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedContent = await req.Content!.ReadAsStringAsync();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = "at", expires_in = 3600 }),
                    Encoding.UTF8,
                    "application/json")
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var service = CreateService(httpClient);
        var provider = TestProviders[0];

        await service.RefreshTokenAsync(provider, "ENC:my-real-refresh-token");

        // Verify Decrypt was called with the encrypted token
        _mockEncryption.Verify(x => x.Decrypt("ENC:my-real-refresh-token"), Times.Once);

        // Verify the decrypted token was sent in the request
        capturedContent.Should().Contain("refresh_token=my-real-refresh-token");
        capturedContent.Should().Contain("grant_type=refresh_token");
        capturedContent.Should().Contain($"client_id={Uri.EscapeDataString("test-gmail-client-id")}");
    }

    [Fact]
    public async Task RefreshTokenAsync_ErrorResponse_ThrowsHttpRequestException()
    {
        var mockHandler = CreateMockHttpHandler(
            HttpStatusCode.Unauthorized,
            "{\"error\":\"invalid_grant\"}");

        var httpClient = new HttpClient(mockHandler.Object);
        var service = CreateService(httpClient);
        var provider = TestProviders[0];

        var act = () => service.RefreshTokenAsync(provider, "ENC:expired-token");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    #endregion

    #region Token Encryption Verification

    [Fact]
    public async Task ExchangeCodeForTokenAsync_EncryptsTokens()
    {
        var tokenResponse = new
        {
            access_token = "plain-access",
            refresh_token = "plain-refresh",
            expires_in = 3600
        };

        var mockHandler = CreateMockHttpHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(tokenResponse));

        var httpClient = new HttpClient(mockHandler.Object);
        var service = CreateService(httpClient);
        var provider = TestProviders[0];

        var result = await service.ExchangeCodeForTokenAsync(
            provider, "code", "verifier", "http://localhost:12345/");

        // Encrypted tokens should not equal plaintext
        result.AccessToken.Should().NotBe("plain-access");
        result.RefreshToken.Should().NotBe("plain-refresh");

        // Verify Encrypt was called
        _mockEncryption.Verify(x => x.Encrypt("plain-access"), Times.Once);
        _mockEncryption.Verify(x => x.Encrypt("plain-refresh"), Times.Once);
    }

    #endregion

    #region Provider Loading

    [Fact]
    public void Constructor_MissingProvidersFile_ThrowsFileNotFoundException()
    {
        var act = () => new OAuthService(
            new HttpClient(),
            _mockEncryption.Object,
            _mockLogger.Object,
            "/nonexistent/path/oauth-providers.json");

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Constructor_LoadsAllProviders()
    {
        var service = CreateService();

        // Verify all providers are loaded by checking each host
        service.FindProviderByHost("imap.gmail.com").Should().NotBeNull();
        service.FindProviderByHost("outlook.office365.com").Should().NotBeNull();
    }

    #endregion

    #region Helpers

    private static Mock<HttpMessageHandler> CreateMockHttpHandler(HttpStatusCode statusCode, string responseContent)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            });

        return mockHandler;
    }

    #endregion
}
