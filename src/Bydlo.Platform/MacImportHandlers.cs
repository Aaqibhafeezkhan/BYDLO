using System.Runtime.InteropServices;
using System.Text;

namespace Bydlo.Platform;

internal interface IMacImportHandlers
{
    string? DefaultHandler(string scheme);
    void SetDefaultHandler(string scheme, string bundleId);
}

internal sealed class MacImportHandlers : IMacImportHandlers
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const uint Utf8 = 0x08000100;

    public string? DefaultHandler(string scheme)
    {
        if (!OperatingSystem.IsMacOS()) { throw new PlatformNotSupportedException(); }
        var key = CreateString(scheme);
        try
        {
            var value = LSCopyDefaultHandlerForURLScheme(key);
            if (value == IntPtr.Zero) { return null; }
            try
            {
                var buffer = new byte[4096];
                if (!CFStringGetCString(value, buffer, buffer.Length, Utf8)) { throw new IOException("Could not read protocol handler."); }
                return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
            }
            finally { CFRelease(value); }
        }
        finally { CFRelease(key); }
    }

    public void SetDefaultHandler(string scheme, string bundleId)
    {
        if (!OperatingSystem.IsMacOS()) { throw new PlatformNotSupportedException(); }
        var key = CreateString(scheme);
        var value = CreateString(bundleId);
        try
        {
            if (LSSetDefaultHandlerForURLScheme(key, value) != 0) { throw new IOException("Could not set protocol handler."); }
        }
        finally { CFRelease(value); CFRelease(key); }
    }

    private static IntPtr CreateString(string value)
    {
        var result = CFStringCreateWithCString(IntPtr.Zero, Encoding.UTF8.GetBytes(value + '\0'), Utf8);
        return result != IntPtr.Zero ? result : throw new IOException("Could not create protocol identifier.");
    }

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] value, uint encoding);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
    [DllImport(CoreServices)]
    private static extern IntPtr LSCopyDefaultHandlerForURLScheme(IntPtr scheme);
    [DllImport(CoreServices)]
    private static extern int LSSetDefaultHandlerForURLScheme(IntPtr scheme, IntPtr bundleId);
}
