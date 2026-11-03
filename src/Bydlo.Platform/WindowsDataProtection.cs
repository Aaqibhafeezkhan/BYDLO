using System.Security.Cryptography;
using System.Text;

namespace Bydlo.Platform;

public interface IDataProtection
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] cipher);
}

/// <summary>The only DPAPI boundary. Persistent entropy and scope must remain compatible.</summary>
public sealed class WindowsDataProtection : IDataProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BYDLO.secrets.v1");

    public byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        return ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
    }
}
