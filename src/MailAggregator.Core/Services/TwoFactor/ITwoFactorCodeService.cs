using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.TwoFactor;

public record OtpAuthParameters(
    string Secret,
    string Issuer,
    string Label,
    OtpAlgorithm Algorithm,
    int Digits,
    int Period);

public interface ITwoFactorCodeService
{
    string GenerateCode(string base32Secret, OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6, int period = 30);
    int GetRemainingSeconds(int period = 30);
    OtpAuthParameters ParseOtpAuthUri(string uri);
}
