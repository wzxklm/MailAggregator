using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.TwoFactor;

public interface ITwoFactorAccountService
{
    Task<TwoFactorAccount> AddAsync(string issuer, string label, string base32Secret,
        OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6, int period = 30,
        CancellationToken cancellationToken = default);

    Task<TwoFactorAccount> AddFromUriAsync(string otpAuthUri, CancellationToken cancellationToken = default);

    Task<TwoFactorAccount> UpdateAsync(int id, string issuer, string label, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TwoFactorAccount>> GetAllAsync(CancellationToken cancellationToken = default);

    string GetDecryptedSecret(TwoFactorAccount account);
}
