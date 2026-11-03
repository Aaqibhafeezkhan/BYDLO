using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bydlo.Platform;

[SupportedOSPlatform("linux")]
internal sealed partial class LinuxSecretBackend(string scope) : ISystemSecretBackend
{
    private const string Secret = "libsecret-1.so.0";
    private const string Glib = "libglib-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private static readonly Lazy<nint> GlibHandle = new(() => NativeLibrary.Load(Glib));

    public string Description => "Linux Secret Service (libsecret)";

    public Task<string?> ReadAsync(string id, CancellationToken ct) => ExecuteAsync(id, (table, cancellable) =>
    {
        var secret = Lookup(0, table, cancellable, out var error);
        try
        {
            Check(error, ct);
            return secret == 0 ? null : Marshal.PtrToStringUTF8(secret);
        }
        finally
        {
            if (secret != 0) { PasswordFree(secret); }
        }
    }, ct);

    public Task WriteAsync(string id, string value, CancellationToken ct) => ExecuteAsync(id, (table, cancellable) =>
    {
        var ok = Store(0, table, 0, "BYDLO profile secret", value, cancellable, out var error);
        Check(error, ct);
        if (ok == 0) { throw new SystemSecretUnavailableException(); }
        return true;
    }, ct);

    public Task DeleteAsync(string id, CancellationToken ct) => ExecuteAsync(id, (table, cancellable) =>
    {
        Clear(0, table, cancellable, out var error);
        Check(error, ct);
        return true;
    }, ct);

    private Task<T> ExecuteAsync<T>(string id, Func<nint, nint, T> run, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var hash = NativeLibrary.GetExport(GlibHandle.Value, "g_str_hash");
            var equal = NativeLibrary.GetExport(GlibHandle.Value, "g_str_equal");
            var table = HashNew(hash, equal);
            var key = Marshal.StringToCoTaskMemUTF8("bydlo-secret-id");
            var value = Marshal.StringToCoTaskMemUTF8(scope + ":" + id);
            try
            {
                _ = HashInsert(table, key, value);
                var cancellable = CancellableNew();
                try
                {
                    using var registration = ct.Register(() => CancellableCancel(cancellable));
                    return run(table, cancellable);
                }
                finally
                {
                    ObjectUnref(cancellable);
                }
            }
            finally
            {
                HashUnref(table);
                Marshal.FreeCoTaskMem(key);
                Marshal.FreeCoTaskMem(value);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SystemSecretUnavailableException();
        }
    }, ct);

    private static void Check(nint error, CancellationToken ct)
    {
        if (error != 0)
        {
            ErrorFree(error);
            ct.ThrowIfCancellationRequested();
            throw new SystemSecretUnavailableException();
        }
    }

    [LibraryImport(Secret, EntryPoint = "secret_password_lookupv_sync")]
    private static partial nint Lookup(nint schema, nint table, nint cancellable, out nint error);

    [LibraryImport(Secret, EntryPoint = "secret_password_storev_sync", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Store(nint schema, nint table, nint collection, string label, string value, nint cancellable, out nint error);

    [LibraryImport(Secret, EntryPoint = "secret_password_clearv_sync")]
    private static partial int Clear(nint schema, nint table, nint cancellable, out nint error);

    [LibraryImport(Secret, EntryPoint = "secret_password_free")]
    private static partial void PasswordFree(nint password);

    [LibraryImport(Glib, EntryPoint = "g_hash_table_new")]
    private static partial nint HashNew(nint hash, nint equal);

    [LibraryImport(Glib, EntryPoint = "g_hash_table_insert")]
    private static partial int HashInsert(nint table, nint key, nint value);

    [LibraryImport(Glib, EntryPoint = "g_hash_table_unref")]
    private static partial void HashUnref(nint table);

    [LibraryImport(Glib, EntryPoint = "g_error_free")]
    private static partial void ErrorFree(nint error);

    [LibraryImport(Gio, EntryPoint = "g_cancellable_new")]
    private static partial nint CancellableNew();

    [LibraryImport(Gio, EntryPoint = "g_cancellable_cancel")]
    private static partial void CancellableCancel(nint cancellable);

    [LibraryImport(GObject, EntryPoint = "g_object_unref")]
    private static partial void ObjectUnref(nint obj);
}
