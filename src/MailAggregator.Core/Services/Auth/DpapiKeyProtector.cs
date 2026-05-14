using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace MailAggregator.Core.Services.Auth;

/// <summary>
/// Protects the AES master key using Windows DPAPI, binding it to the current user.
/// Only usable on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public class DpapiKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] data)
    {
        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] data)
    {
        return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }
}
