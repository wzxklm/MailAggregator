namespace MailAggregator.Core.Models;

public class TwoFactorAccount
{
    public int Id { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string EncryptedSecret { get; set; } = string.Empty;
    public OtpAlgorithm Algorithm { get; set; } = OtpAlgorithm.Sha1;
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
