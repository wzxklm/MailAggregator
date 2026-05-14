using System.Net;
using System.Text;
using FluentAssertions;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Discovery;
using Moq;
using Moq.Protected;
using Serilog;

namespace MailAggregator.Tests.Services.Discovery;

public class AutoDiscoveryServiceTests
{
    private const string ValidAutoconfigXml = """
        <clientConfig>
          <emailProvider id="example.com">
            <incomingServer type="imap">
              <hostname>imap.example.com</hostname>
              <port>993</port>
              <socketType>SSL</socketType>
              <authentication>password-cleartext</authentication>
            </incomingServer>
            <outgoingServer type="smtp">
              <hostname>smtp.example.com</hostname>
              <port>587</port>
              <socketType>STARTTLS</socketType>
              <authentication>password-cleartext</authentication>
            </outgoingServer>
          </emailProvider>
        </clientConfig>
        """;

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static HttpClient CreateHttpClient(MockHttpMessageHandler handler)
    {
        return new HttpClient(handler);
    }

    #region Level 1 - autoconfig.{domain}

    [Fact]
    public async Task DiscoverAsync_Level1_Succeeds_ReturnsConfig()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("https://autoconfig.example.com/mail/config-v1.1.xml", HttpStatusCode.OK, ValidAutoconfigXml);

        var client = CreateHttpClient(handler);
        var service = new AutoDiscoveryService(client, Logger);

        var config = await service.DiscoverAsync("user@example.com");

        config.Should().NotBeNull();
        config!.ImapHost.Should().Be("imap.example.com");
        config.ImapPort.Should().Be(993);
        config.ImapEncryption.Should().Be(ConnectionEncryptionType.Ssl);
        config.SmtpHost.Should().Be("smtp.example.com");
        config.SmtpPort.Should().Be(587);
        config.SmtpEncryption.Should().Be(ConnectionEncryptionType.StartTls);
    }

    #endregion

    #region Level 2 - well-known path

    [Fact]
    public async Task DiscoverAsync_Level2_FallbackFromLevel1_ReturnsConfig()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("https://autoconfig.example.com/mail/config-v1.1.xml", HttpStatusCode.NotFound);
        handler.SetResponse("https://example.com/.well-known/autoconfig/mail/config-v1.1.xml", HttpStatusCode.OK, ValidAutoconfigXml);

        var client = CreateHttpClient(handler);
        var service = new AutoDiscoveryService(client, Logger);

        var config = await service.DiscoverAsync("user@example.com");

        config.Should().NotBeNull();
        config!.ImapHost.Should().Be("imap.example.com");
    }

    #endregion

    #region Level 3 - Thunderbird ISPDB

    [Fact]
    public async Task DiscoverAsync_Level3_FallbackFromLevels1And2_ReturnsConfig()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("https://autoconfig.example.com/mail/config-v1.1.xml", HttpStatusCode.NotFound);
        handler.SetResponse("https://example.com/.well-known/autoconfig/mail/config-v1.1.xml", HttpStatusCode.NotFound);
        handler.SetResponse("https://autoconfig.thunderbird.net/v1.1/example.com", HttpStatusCode.OK, ValidAutoconfigXml);

        var client = CreateHttpClient(handler);
        var service = new AutoDiscoveryService(client, Logger);

        var config = await service.DiscoverAsync("user@example.com");

        config.Should().NotBeNull();
        config!.ImapHost.Should().Be("imap.example.com");
    }

    #endregion

    #region Level 4 - MX record fallback

    [Fact]
    public async Task DiscoverAsync_Level4_MxFallback_ReturnsConfig()
    {
        var handler = new MockHttpMessageHandler();
        // All levels fail for original domain
        handler.SetDefaultResponse(HttpStatusCode.NotFound);
        // Level 1 succeeds for MX domain
        handler.SetResponse("https://autoconfig.mxprovider.com/mail/config-v1.1.xml", HttpStatusCode.OK, ValidAutoconfigXml);

        var client = CreateHttpClient(handler);
        var service = new TestableAutoDiscoveryService(client, Logger, "mxprovider.com");

        var config = await service.DiscoverAsync("user@obscure-domain.com");

        config.Should().NotBeNull();
        config!.ImapHost.Should().Be("imap.example.com");
    }

    #endregion

    #region Level 5 - returns null

    [Fact]
    public async Task DiscoverAsync_AllLevelsFail_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetDefaultResponse(HttpStatusCode.NotFound);

        var client = CreateHttpClient(handler);
        var service = new TestableAutoDiscoveryService(client, Logger, null);

        var config = await service.DiscoverAsync("user@unknown-domain.com");

        config.Should().BeNull();
    }

    #endregion

    #region XML Parsing - socketType values

    [Theory]
    [InlineData("SSL", ConnectionEncryptionType.Ssl)]
    [InlineData("STARTTLS", ConnectionEncryptionType.StartTls)]
    [InlineData("PLAIN", ConnectionEncryptionType.None)]
    [InlineData("plain", ConnectionEncryptionType.None)]
    [InlineData("", ConnectionEncryptionType.None)]
    public void ParseSocketType_MapsCorrectly(string socketType, ConnectionEncryptionType expected)
    {
        var result = AutoDiscoveryService.ParseSocketType(socketType);
        result.Should().Be(expected);
    }

    [Fact]
    public void ParseSocketType_NullInput_ReturnsNone()
    {
        var result = AutoDiscoveryService.ParseSocketType(null);
        result.Should().Be(ConnectionEncryptionType.None);
    }

    [Fact]
    public void ParseAutoconfigXml_ValidXml_ReturnsConfig()
    {
        var config = AutoDiscoveryService.ParseAutoconfigXml(ValidAutoconfigXml);

        config.Should().NotBeNull();
        config!.ImapHost.Should().Be("imap.example.com");
        config.ImapPort.Should().Be(993);
        config.ImapEncryption.Should().Be(ConnectionEncryptionType.Ssl);
        config.SmtpHost.Should().Be("smtp.example.com");
        config.SmtpPort.Should().Be(587);
        config.SmtpEncryption.Should().Be(ConnectionEncryptionType.StartTls);
    }

    [Fact]
    public void ParseAutoconfigXml_PlainSocketType_ReturnsNoneEncryption()
    {
        var xml = """
            <clientConfig>
              <emailProvider id="example.com">
                <incomingServer type="imap">
                  <hostname>imap.example.com</hostname>
                  <port>143</port>
                  <socketType>PLAIN</socketType>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.example.com</hostname>
                  <port>25</port>
                  <socketType>plain</socketType>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        var config = AutoDiscoveryService.ParseAutoconfigXml(xml);

        config.Should().NotBeNull();
        config!.ImapPort.Should().Be(143);
        config.ImapEncryption.Should().Be(ConnectionEncryptionType.None);
        config.SmtpPort.Should().Be(25);
        config.SmtpEncryption.Should().Be(ConnectionEncryptionType.None);
    }

    [Fact]
    public void ParseAutoconfigXml_NoImapHost_ReturnsNull()
    {
        var xml = """
            <clientConfig>
              <emailProvider id="example.com">
                <outgoingServer type="smtp">
                  <hostname>smtp.example.com</hostname>
                  <port>587</port>
                  <socketType>STARTTLS</socketType>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        var config = AutoDiscoveryService.ParseAutoconfigXml(xml);
        config.Should().BeNull();
    }

    [Fact]
    public void ParseAutoconfigXml_EmptyOrNull_ReturnsNull()
    {
        AutoDiscoveryService.ParseAutoconfigXml("").Should().BeNull();
        AutoDiscoveryService.ParseAutoconfigXml(null!).Should().BeNull();
    }

    [Fact]
    public void ParseAutoconfigXml_InvalidXml_ThrowsOrReturnsNull()
    {
        // XDocument.Parse will throw on invalid XML; ParseAutoconfigXml is called
        // inside TryFetchAndParseAsync which catches exceptions.
        var act = () => AutoDiscoveryService.ParseAutoconfigXml("<not valid xml");
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void ParseAutoconfigXml_NoEmailProvider_ReturnsNull()
    {
        var xml = "<clientConfig></clientConfig>";
        var config = AutoDiscoveryService.ParseAutoconfigXml(xml);
        config.Should().BeNull();
    }

    #endregion

    #region Invalid email address

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notanemail")]
    [InlineData("@nodomain")]
    [InlineData("user@")]
    [InlineData("user@nodot")]
    public async Task DiscoverAsync_InvalidEmail_ReturnsNull(string email)
    {
        var handler = new MockHttpMessageHandler();
        handler.SetDefaultResponse(HttpStatusCode.NotFound);
        var client = CreateHttpClient(handler);
        var service = new AutoDiscoveryService(client, Logger);

        var config = await service.DiscoverAsync(email);

        config.Should().BeNull();
    }

    [Fact]
    public async Task DiscoverAsync_NullEmail_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetDefaultResponse(HttpStatusCode.NotFound);
        var client = CreateHttpClient(handler);
        var service = new AutoDiscoveryService(client, Logger);

        var config = await service.DiscoverAsync(null!);

        config.Should().BeNull();
    }

    #endregion

    #region HTTP exceptions don't crash

    [Fact]
    public async Task DiscoverAsync_HttpRequestThrows_DoesNotCrash_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetExceptionForAll(new HttpRequestException("Network error"));

        var client = CreateHttpClient(handler);
        var service = new TestableAutoDiscoveryService(client, Logger, null);

        var config = await service.DiscoverAsync("user@example.com");

        config.Should().BeNull();
    }

    [Fact]
    public async Task DiscoverAsync_TimeoutException_DoesNotCrash_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetExceptionForAll(new TaskCanceledException("Request timed out",
            new TimeoutException("Timeout")));

        var client = CreateHttpClient(handler);
        var service = new TestableAutoDiscoveryService(client, Logger, null);

        // Use a non-cancelled token so the TaskCanceledException is treated as a timeout, not caller cancellation
        var config = await service.DiscoverAsync("user@example.com", CancellationToken.None);

        config.Should().BeNull();
    }

    #endregion

    #region Domain extraction (ExtractBaseDomain)

    [Fact]
    public void ExtractBaseDomain_StandardMxHost_ExtractsDomain()
    {
        var domain = AutoDiscoveryService.ExtractBaseDomain("alt1.gmail-smtp-in.l.google.com");
        domain.Should().Be("google.com");
    }

    [Theory]
    [InlineData("mx1.mail.yahoo.co.uk", "yahoo.co.uk")]
    [InlineData("mail.example.co.jp", "example.co.jp")]
    [InlineData("smtp.provider.com.cn", "provider.com.cn")]
    [InlineData("mx.host.com.au", "host.com.au")]
    public void ExtractBaseDomain_CountryTwoLevelTld_ExtractsCorrectDomain(string mxHost, string expected)
    {
        var domain = AutoDiscoveryService.ExtractBaseDomain(mxHost);
        domain.Should().Be(expected);
    }

    [Fact]
    public void ExtractBaseDomain_RegularTld_Works()
    {
        var domain = AutoDiscoveryService.ExtractBaseDomain("mx.mailprovider.com");
        domain.Should().Be("mailprovider.com");
    }

    [Fact]
    public void ExtractBaseDomain_EmptyOrNull_ReturnsNull()
    {
        AutoDiscoveryService.ExtractBaseDomain("").Should().BeNull();
        AutoDiscoveryService.ExtractBaseDomain(null!).Should().BeNull();
    }

    [Fact]
    public void ExtractBaseDomain_SimpleTwoPartDomain_ReturnsSame()
    {
        AutoDiscoveryService.ExtractBaseDomain("google.com").Should().Be("google.com");
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task DiscoverAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, ValidAutoconfigXml);

        var client = CreateHttpClient(handler);
        var service = new AutoDiscoveryService(client, Logger);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => service.DiscoverAsync("user@example.com", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Test helpers

    /// <summary>
    /// Subclass that overrides DNS resolution to avoid real DNS lookups in tests.
    /// </summary>
    private class TestableAutoDiscoveryService : AutoDiscoveryService
    {
        private readonly string? _mxDomain;

        public TestableAutoDiscoveryService(HttpClient httpClient, ILogger logger, string? mxDomain)
            : base(httpClient, logger)
        {
            _mxDomain = mxDomain;
        }

        protected internal override Task<string?> ResolveMxDomainAsync(string domain, CancellationToken cancellationToken)
        {
            return Task.FromResult(_mxDomain);
        }

        protected internal override Task<ServerConfiguration?> TryDiscoverViaSrvAsync(string domain, CancellationToken cancellationToken)
        {
            return Task.FromResult<ServerConfiguration?>(null);
        }
    }

    /// <summary>
    /// A mock HttpMessageHandler that returns preconfigured responses based on request URL.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode StatusCode, string? Content)> _responses = new();
        private HttpStatusCode _defaultStatusCode = HttpStatusCode.NotFound;
        private string? _defaultContent;
        private Exception? _exceptionForAll;

        public void SetResponse(string url, HttpStatusCode statusCode, string? content = null)
        {
            _responses[url] = (statusCode, content);
        }

        public void SetDefaultResponse(HttpStatusCode statusCode, string? content = null)
        {
            _defaultStatusCode = statusCode;
            _defaultContent = content;
        }

        public void SetExceptionForAll(Exception exception)
        {
            _exceptionForAll = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_exceptionForAll != null)
                throw _exceptionForAll;

            var url = request.RequestUri?.ToString() ?? string.Empty;

            HttpStatusCode statusCode;
            string? content;

            if (_responses.TryGetValue(url, out var configured))
            {
                statusCode = configured.StatusCode;
                content = configured.Content;
            }
            else
            {
                statusCode = _defaultStatusCode;
                content = _defaultContent;
            }

            var response = new HttpResponseMessage(statusCode);
            if (content != null)
            {
                response.Content = new StringContent(content, Encoding.UTF8, "application/xml");
            }

            return Task.FromResult(response);
        }
    }

    #endregion
}
