namespace MailAggregator.Core.Models;

public class OAuthTokenResult
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
