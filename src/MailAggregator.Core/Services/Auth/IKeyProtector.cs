namespace MailAggregator.Core.Services.Auth;

/// <summary>
/// Abstracts platform-specific key protection.
/// Windows: DPAPI via ProtectedData.
/// Linux (dev/test only): fixed key fallback.
/// </summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}
