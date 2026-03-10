using System.Security.Cryptography;
using MailAggregator.Core.Models;
using OtpNet;

namespace MailAggregator.Core.Services.TwoFactor;

public class TwoFactorCodeService : ITwoFactorCodeService
{
    public string GenerateCode(string base32Secret, OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6, int period = 30)
    {
        if (string.IsNullOrWhiteSpace(base32Secret))
            throw new ArgumentException("Secret cannot be null or empty.", nameof(base32Secret));

        var secretBytes = Base32Encoding.ToBytes(base32Secret);
        try
        {
            var totp = new Totp(secretBytes, step: period, mode: MapAlgorithm(algorithm), totpSize: digits);
            return totp.ComputeTotp();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public int GetRemainingSeconds(int period = 30)
    {
        var unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return period - (int)(unixTimestamp % period);
    }

    public OtpAuthParameters ParseOtpAuthUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("URI cannot be null or empty.", nameof(uri));

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "otpauth")
            throw new FormatException("URI must use the otpauth:// scheme.");

        if (!string.Equals(parsed.Host, "totp", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Only TOTP type is supported.");

        // Path is /Label or /Issuer:Label
        var path = Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'));
        if (string.IsNullOrEmpty(path))
            throw new FormatException("URI must contain a label in the path.");

        var query = ParseQuery(parsed.Query);

        if (!query.TryGetValue("secret", out var secret) || string.IsNullOrWhiteSpace(secret))
            throw new FormatException("URI must contain a 'secret' parameter.");

        // Parse issuer: prefer query param, fallback to path prefix
        query.TryGetValue("issuer", out var issuer);
        string label;
        var colonIndex = path.IndexOf(':');
        if (colonIndex >= 0)
        {
            label = path[(colonIndex + 1)..].Trim();
            issuer ??= path[..colonIndex].Trim();
        }
        else
        {
            label = path;
        }
        issuer ??= string.Empty;

        var algorithm = OtpAlgorithm.Sha1;
        if (query.TryGetValue("algorithm", out var algoStr))
        {
            algorithm = algoStr.ToUpperInvariant() switch
            {
                "SHA1" => OtpAlgorithm.Sha1,
                "SHA256" => OtpAlgorithm.Sha256,
                "SHA512" => OtpAlgorithm.Sha512,
                _ => throw new FormatException($"Unsupported algorithm: {algoStr}")
            };
        }

        var digits = 6;
        if (query.TryGetValue("digits", out var digitsStr))
        {
            if (!int.TryParse(digitsStr, out digits) || digits < 6 || digits > 8)
                throw new FormatException($"Invalid digits value: {digitsStr}");
        }

        var period = 30;
        if (query.TryGetValue("period", out var periodStr))
        {
            if (!int.TryParse(periodStr, out period) || period < 1)
                throw new FormatException($"Invalid period value: {periodStr}");
        }

        return new OtpAuthParameters(
            Secret: secret.ToUpperInvariant(),
            Issuer: issuer,
            Label: label,
            Algorithm: algorithm,
            Digits: digits,
            Period: period);
    }

    private static OtpHashMode MapAlgorithm(OtpAlgorithm algorithm) => algorithm switch
    {
        OtpAlgorithm.Sha1 => OtpHashMode.Sha1,
        OtpAlgorithm.Sha256 => OtpHashMode.Sha256,
        OtpAlgorithm.Sha512 => OtpHashMode.Sha512,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return result;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex < 0) continue;
            var key = Uri.UnescapeDataString(pair[..eqIndex]);
            var value = Uri.UnescapeDataString(pair[(eqIndex + 1)..]);
            result[key] = value;
        }
        return result;
    }
}
