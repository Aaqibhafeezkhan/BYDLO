using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Bydlo.Platform;

[SupportedOSPlatform("macos")]
internal sealed partial class MacKeychainBackend(string scope) : ISystemSecretBackend
{
    private const string Security = "Security.framework/Security";
    private const string CoreFoundation = "CoreFoundation.framework/CoreFoundation";
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;

    public string Description => "macOS Keychain";

    public Task<string?> ReadAsync(string id, CancellationToken ct) => ExecuteAsync<string?>(() => Read(id), ct);
    public Task WriteAsync(string id, string value, CancellationToken ct) => ExecuteAsync(() => Write(id, value), ct);
    public Task DeleteAsync(string id, CancellationToken ct) => ExecuteAsync(() => Delete(id), ct);

    private unsafe string? Read(string id)
    {
        uint length = 0;
        nint password = 0;
        var status = Find(0, (uint)Encoding.UTF8.GetByteCount(scope), scope, (uint)Encoding.UTF8.GetByteCount(id), id,
            &length, &password, null);
        try
        {
            return status == ItemNotFound ? null : status == 0
                ? Marshal.PtrToStringUTF8(password, checked((int)length))
                : throw new SystemSecretUnavailableException();
        }
        finally
        {
            if (password != 0) { _ = FreeContent(0, password); }
        }
    }

    private unsafe bool Write(string id, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            fixed (byte* data = bytes)
            {
                var serviceLength = (uint)Encoding.UTF8.GetByteCount(scope);
                var idLength = (uint)Encoding.UTF8.GetByteCount(id);
                var status = Add(0, serviceLength, scope, idLength, id, (uint)bytes.Length, data, null);
                if (status == DuplicateItem)
                {
                    nint item = 0;
                    status = Find(0, serviceLength, scope, idLength, id, null, null, &item);
                    try
                    {
                        Check(status);
                        status = Modify(item, 0, (uint)bytes.Length, data);
                    }
                    finally
                    {
                        if (item != 0) { Release(item); }
                    }
                }
                Check(status);
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private unsafe bool Delete(string id)
    {
        nint item = 0;
        var status = Find(0, (uint)Encoding.UTF8.GetByteCount(scope), scope, (uint)Encoding.UTF8.GetByteCount(id), id,
            null, null, &item);
        try
        {
            if (status != ItemNotFound)
            {
                Check(status);
                status = DeleteItem(item);
                if (status != ItemNotFound) { Check(status); }
            }
            return true;
        }
        finally
        {
            if (item != 0) { Release(item); }
        }
    }

    private static Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SystemSecretUnavailableException();
        }
    }, ct);

    private static void Check(int status)
    {
        if (status != 0) { throw new SystemSecretUnavailableException(); }
    }

    [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int Find(nint keychain, uint serviceLength, string service, uint accountLength, string account,
        uint* passwordLength, nint* passwordData, nint* item);

    [LibraryImport(Security, EntryPoint = "SecKeychainAddGenericPassword", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int Add(nint keychain, uint serviceLength, string service, uint accountLength, string account,
        uint passwordLength, byte* passwordData, nint* item);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    private static unsafe partial int Modify(nint item, nint attributes, uint length, byte* data);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemDelete")]
    private static partial int DeleteItem(nint item);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemFreeContent")]
    private static partial int FreeContent(nint attributes, nint data);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void Release(nint value);
}
