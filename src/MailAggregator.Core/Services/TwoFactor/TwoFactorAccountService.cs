using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MailAggregator.Core.Services.TwoFactor;

public class TwoFactorAccountService : ITwoFactorAccountService
{
    private readonly MailAggregatorDbContext _dbContext;
    private readonly ICredentialEncryptionService _encryptionService;
    private readonly ITwoFactorCodeService _codeService;
    private readonly ILogger _logger;

    public TwoFactorAccountService(
        MailAggregatorDbContext dbContext,
        ICredentialEncryptionService encryptionService,
        ITwoFactorCodeService codeService,
        ILogger logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
        _codeService = codeService ?? throw new ArgumentNullException(nameof(codeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TwoFactorAccount> AddAsync(string issuer, string label, string base32Secret,
        OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6, int period = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issuer))
            throw new ArgumentException("Issuer cannot be empty.", nameof(issuer));
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label cannot be empty.", nameof(label));
        if (string.IsNullOrWhiteSpace(base32Secret))
            throw new ArgumentException("Secret cannot be empty.", nameof(base32Secret));

        var normalizedSecret = base32Secret.ToUpperInvariant();

        // Validate secret is valid base32 by attempting to generate a code
        _codeService.GenerateCode(normalizedSecret, algorithm, digits, period);

        var account = new TwoFactorAccount
        {
            Issuer = issuer,
            Label = label,
            EncryptedSecret = _encryptionService.Encrypt(normalizedSecret),
            Algorithm = algorithm,
            Digits = digits,
            Period = period
        };

        _dbContext.TwoFactorAccounts.Add(account);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.Information("Added 2FA account for {Issuer} ({Label})", issuer, label);
        return account;
    }

    public async Task<TwoFactorAccount> AddFromUriAsync(string otpAuthUri, CancellationToken cancellationToken = default)
    {
        var parameters = _codeService.ParseOtpAuthUri(otpAuthUri);
        return await AddAsync(
            parameters.Issuer,
            parameters.Label,
            parameters.Secret,
            parameters.Algorithm,
            parameters.Digits,
            parameters.Period,
            cancellationToken);
    }

    public async Task<TwoFactorAccount> UpdateAsync(int id, string issuer, string label, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issuer))
            throw new ArgumentException("Issuer cannot be empty.", nameof(issuer));
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label cannot be empty.", nameof(label));

        var account = await _dbContext.TwoFactorAccounts.FindAsync([id], cancellationToken)
            ?? throw new InvalidOperationException($"2FA account with ID {id} not found.");

        account.Issuer = issuer;
        account.Label = label;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.Information("Updated 2FA account {Id} ({Issuer})", id, issuer);
        return account;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var account = await _dbContext.TwoFactorAccounts.FindAsync([id], cancellationToken)
            ?? throw new InvalidOperationException($"2FA account with ID {id} not found.");

        _dbContext.TwoFactorAccounts.Remove(account);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.Information("Deleted 2FA account {Id} ({Issuer})", id, account.Issuer);
    }

    public async Task<IReadOnlyList<TwoFactorAccount>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await _dbContext.TwoFactorAccounts
            .AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        return accounts.AsReadOnly();
    }

    public string GetDecryptedSecret(TwoFactorAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (string.IsNullOrEmpty(account.EncryptedSecret))
            throw new InvalidOperationException("Account has no encrypted secret.");

        return _encryptionService.Decrypt(account.EncryptedSecret);
    }
}
