using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

/// <summary>Base: one encrypted file per secret id, written atomically.</summary>
public abstract class FileSecretStoreBase(string directory) : ISecretStore
{
    protected string Directory { get; } = directory;

    public abstract string ProtectionDescription { get; }

    protected abstract byte[] Protect(byte[] plain);

    protected abstract byte[] Unprotect(byte[] cipher);

    public async Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets.Values, PlatformJsonContext.Default.DictionaryStringString);
        try
        {
            var path = PathFor(id);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, Protect(plain), ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // Store-owned serialized plaintext: clear on success, error and cancellation.
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return ProfileSecrets.Empty;
        }

        byte[] cipher;
        try
        {
            cipher = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretAccessException(id, ex);
        }

        try
        {
            // Ownership: Unprotect hands the plaintext buffer to this method on success;
            // this method clears it after deserialization, on every path below.
            var plain = Unprotect(cipher);
            try
            {
                var values = JsonSerializer.Deserialize(plain, PlatformJsonContext.Default.DictionaryStringString) ?? [];
                return new ProfileSecrets { Values = new(values, StringComparer.OrdinalIgnoreCase) };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new SecretAccessException(id, ex);
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>File name is a hash of the id, so ids (and subscription names) are not visible on disk.</summary>
    private string PathFor(string id) =>
        Path.Combine(Directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32] + ".bin");
}

/// <summary>Windows DPAPI (CurrentUser scope): secrets are bound to the Windows account.</summary>
public sealed class DpapiSecretStore(string directory, IDataProtection? protection = null) : FileSecretStoreBase(directory)
{
    private readonly IDataProtection _protection = protection ?? new WindowsDataProtection();

    public override string ProtectionDescription => "Windows DPAPI (current user)";

    protected override byte[] Protect(byte[] plain) => _protection.Protect(plain);

    protected override byte[] Unprotect(byte[] cipher) => _protection.Unprotect(cipher);
}

/// <summary>
/// AES-256-GCM with a random key stored in an owner-only (0600) key file. Protects against casual disclosure and
/// copied profile folders, not against malware running as the same user. Fallback when the system vault is unavailable.
/// </summary>
public sealed class AesFileSecretStore(string directory) : FileSecretStoreBase(directory)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private byte[]? _key;

    public override string ProtectionDescription => "AES-256-GCM, key file readable by the current user only";

    protected override byte[] Protect(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    protected override byte[] Unprotect(byte[] cipher)
    {
        if (cipher.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Secret file is truncated.");
        }

        // Owned here until a successful return hands it to the caller.
        var plain = new byte[cipher.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(Key(), TagSize);
            aes.Decrypt(cipher.AsSpan(0, NonceSize), cipher.AsSpan(NonceSize + TagSize), cipher.AsSpan(NonceSize, TagSize), plain);
            return plain;
        }
        catch
        {
            // Authentication (or key) failure: never return a partially decrypted buffer.
            CryptographicOperations.ZeroMemory(plain);
            throw;
        }
    }

    private byte[] Key()
    {
        if (_key is not null)
        {
            return _key;
        }

        System.IO.Directory.CreateDirectory(Directory);
        var keyPath = Path.Combine(Directory, "master.key");
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
            return _key;
        }

        _key = RandomNumberGenerator.GetBytes(32);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(keyPath, _key);
        }
        else
        {
            using var fs = new FileStream(keyPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            fs.Write(_key);
        }

        return _key;
    }
}
