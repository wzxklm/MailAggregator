using FluentAssertions;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.TwoFactor;

namespace MailAggregator.Tests.Services.TwoFactor;

public class TwoFactorCodeServiceTests
{
    private readonly TwoFactorCodeService _service = new();

    // RFC 6238 test secret: "12345678901234567890" = Base32 "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
    private const string RfcBase32Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    #region GenerateCode

    [Fact]
    public void GenerateCode_DefaultParams_Returns6DigitCode()
    {
        var code = _service.GenerateCode(RfcBase32Secret);

        code.Should().HaveLength(6);
        code.Should().MatchRegex(@"^\d{6}$");
    }

    [Fact]
    public void GenerateCode_8Digits_Returns8DigitCode()
    {
        var code = _service.GenerateCode(RfcBase32Secret, digits: 8);

        code.Should().HaveLength(8);
        code.Should().MatchRegex(@"^\d{8}$");
    }

    [Fact]
    public void GenerateCode_Sha256_ReturnsValidCode()
    {
        var code = _service.GenerateCode(RfcBase32Secret, OtpAlgorithm.Sha256);

        code.Should().HaveLength(6);
        code.Should().MatchRegex(@"^\d{6}$");
    }

    [Fact]
    public void GenerateCode_Sha512_ReturnsValidCode()
    {
        var code = _service.GenerateCode(RfcBase32Secret, OtpAlgorithm.Sha512);

        code.Should().HaveLength(6);
        code.Should().MatchRegex(@"^\d{6}$");
    }

    [Fact]
    public void GenerateCode_EmptySecret_ThrowsArgumentException()
    {
        var act = () => _service.GenerateCode("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GenerateCode_NullSecret_ThrowsArgumentException()
    {
        var act = () => _service.GenerateCode(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GenerateCode_LowercaseSecret_Works()
    {
        // Base32 decoding should be case-insensitive
        var codeLower = _service.GenerateCode(RfcBase32Secret.ToLowerInvariant());
        var codeUpper = _service.GenerateCode(RfcBase32Secret);

        codeLower.Should().Be(codeUpper);
    }

    [Fact]
    public void GenerateCode_ConsecutiveCallsSamePeriod_ReturnSameCode()
    {
        var code1 = _service.GenerateCode(RfcBase32Secret);
        var code2 = _service.GenerateCode(RfcBase32Secret);

        // Within the same 30-second window, codes should be identical
        code1.Should().Be(code2);
    }

    #endregion

    #region GetRemainingSeconds

    [Fact]
    public void GetRemainingSeconds_Default30_ReturnsBetween1And30()
    {
        var remaining = _service.GetRemainingSeconds();

        remaining.Should().BeInRange(1, 30);
    }

    [Fact]
    public void GetRemainingSeconds_60Period_ReturnsBetween1And60()
    {
        var remaining = _service.GetRemainingSeconds(60);

        remaining.Should().BeInRange(1, 60);
    }

    #endregion

    #region ParseOtpAuthUri

    [Fact]
    public void ParseOtpAuthUri_FullUri_ParsesAllFields()
    {
        var uri = "otpauth://totp/GitHub:user@example.com?secret=JBSWY3DPEHPK3PXP&issuer=GitHub&algorithm=SHA256&digits=8&period=60";

        var result = _service.ParseOtpAuthUri(uri);

        result.Secret.Should().Be("JBSWY3DPEHPK3PXP");
        result.Issuer.Should().Be("GitHub");
        result.Label.Should().Be("user@example.com");
        result.Algorithm.Should().Be(OtpAlgorithm.Sha256);
        result.Digits.Should().Be(8);
        result.Period.Should().Be(60);
    }

    [Fact]
    public void ParseOtpAuthUri_MinimalUri_UsesDefaults()
    {
        var uri = "otpauth://totp/myuser?secret=JBSWY3DPEHPK3PXP";

        var result = _service.ParseOtpAuthUri(uri);

        result.Secret.Should().Be("JBSWY3DPEHPK3PXP");
        result.Issuer.Should().BeEmpty();
        result.Label.Should().Be("myuser");
        result.Algorithm.Should().Be(OtpAlgorithm.Sha1);
        result.Digits.Should().Be(6);
        result.Period.Should().Be(30);
    }

    [Fact]
    public void ParseOtpAuthUri_IssuerFromPath_WhenNoQueryIssuer()
    {
        var uri = "otpauth://totp/Google:alice@gmail.com?secret=JBSWY3DPEHPK3PXP";

        var result = _service.ParseOtpAuthUri(uri);

        result.Issuer.Should().Be("Google");
        result.Label.Should().Be("alice@gmail.com");
    }

    [Fact]
    public void ParseOtpAuthUri_QueryIssuerOverridesPathIssuer()
    {
        var uri = "otpauth://totp/OldIssuer:user?secret=JBSWY3DPEHPK3PXP&issuer=NewIssuer";

        var result = _service.ParseOtpAuthUri(uri);

        result.Issuer.Should().Be("NewIssuer");
    }

    [Fact]
    public void ParseOtpAuthUri_EncodedLabel_DecodesCorrectly()
    {
        var uri = "otpauth://totp/My%20Service:user%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=My%20Service";

        var result = _service.ParseOtpAuthUri(uri);

        result.Issuer.Should().Be("My Service");
        result.Label.Should().Be("user@example.com");
    }

    [Fact]
    public void ParseOtpAuthUri_LowercaseAlgorithm_ParsesCorrectly()
    {
        var uri = "otpauth://totp/Test:user?secret=JBSWY3DPEHPK3PXP&algorithm=sha512";

        var result = _service.ParseOtpAuthUri(uri);

        result.Algorithm.Should().Be(OtpAlgorithm.Sha512);
    }

    [Fact]
    public void ParseOtpAuthUri_EmptyUri_ThrowsArgumentException()
    {
        var act = () => _service.ParseOtpAuthUri("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseOtpAuthUri_WrongScheme_ThrowsFormatException()
    {
        var act = () => _service.ParseOtpAuthUri("https://example.com");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseOtpAuthUri_HotpType_ThrowsFormatException()
    {
        var act = () => _service.ParseOtpAuthUri("otpauth://hotp/Test?secret=JBSWY3DPEHPK3PXP");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseOtpAuthUri_NoSecret_ThrowsFormatException()
    {
        var act = () => _service.ParseOtpAuthUri("otpauth://totp/Test?issuer=Foo");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseOtpAuthUri_InvalidAlgorithm_ThrowsFormatException()
    {
        var act = () => _service.ParseOtpAuthUri("otpauth://totp/Test?secret=JBSWY3DPEHPK3PXP&algorithm=MD5");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseOtpAuthUri_InvalidDigits_ThrowsFormatException()
    {
        var act = () => _service.ParseOtpAuthUri("otpauth://totp/Test?secret=JBSWY3DPEHPK3PXP&digits=4");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseOtpAuthUri_SecretNormalization_UppercaseOutput()
    {
        var uri = "otpauth://totp/Test?secret=jbswy3dpehpk3pxp";

        var result = _service.ParseOtpAuthUri(uri);

        result.Secret.Should().Be("JBSWY3DPEHPK3PXP");
    }

    #endregion
}
