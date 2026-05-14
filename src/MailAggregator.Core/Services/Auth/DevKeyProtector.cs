namespace MailAggregator.Core.Services.Auth;

/// <summary>
/// Development/test-only key protector that passes data through unchanged.
/// NOT secure — only for Linux dev environment where DPAPI is unavailable.
/// </summary>
public class DevKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}
