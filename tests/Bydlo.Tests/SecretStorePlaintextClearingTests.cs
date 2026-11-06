using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Platform;

namespace Bydlo.Tests;

/// <summary>
/// Fault-injection tests for issue #395: buffers owned by the file secret store
/// (serialized JSON plaintext) are zeroed on success, error and cancellation paths.
/// The fake <see cref="IDataProtection"/> captures the operation-owned buffers so the
/// tests verify clearing directly; they never scan memory and never use real secrets.
/// </summary>
public sealed class SecretStorePlaintextClearingTests
{
    private sealed class CapturingProtection : IDataProtection
    {
        public List<byte[]> ProtectInputs { get; } = [];

        public List<byte[]> ProtectOutputs { get; } = [];

        public List<byte[]> UnprotectOutputs { get; } = [];

        public Func<byte[], byte[]>? OnProtect { get; set; }

        public Func<byte[], byte[]>? OnUnprotect { get; set; }

        public byte[] Protect(byte[] plain)
        {
            ProtectInputs.Add(plain);
            var cipher = OnProtect is null ? (byte[])plain.Clone() : OnProtect(plain);
            ProtectOutputs.Add(cipher);
            return cipher;
        }

        public byte[] Unprotect(byte[] cipher)
        {
            var plain = OnUnprotect is null ? (byte[])cipher.Clone() : OnUnprotect(cipher);
            UnprotectOutputs.Add(plain);
            return plain;
        }
    }

    private static ProfileSecrets SampleSecrets() =>
        new() { Values = { ["uuid"] = "00000000-0000-0000-0000-000000000001", ["note"] = "synthetic-marker-395" } };

    private static void AssertAllZero(byte[] buffer)
    {
        Assert.NotEmpty(buffer);
        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Save_clears_plaintext_when_protect_throws()
    {
        var protection = new CapturingProtection
        {
            OnProtect = _ => throw new CryptographicException("Simulated protect failure."),
        };
        var store = new DpapiSecretStore(TestDirs.New("secret-clear-protect"), protection);

        // Save performs no error wrapping: the backend failure propagates unchanged (#344/#375).
        var error = await Assert.ThrowsAsync<CryptographicException>(() => store.SaveAsync("p1", SampleSecrets()));
        Assert.Equal("Simulated protect failure.", error.Message);
        Assert.Single(protection.ProtectInputs);
        AssertAllZero(protection.ProtectInputs[0]);
    }

    [Fact]
    public async Task Save_clears_plaintext_when_write_is_cancelled()
    {
        var protection = new CapturingProtection();
        var store = new DpapiSecretStore(TestDirs.New("secret-clear-cancel"), protection);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync("p1", SampleSecrets(), cts.Token));
        Assert.Single(protection.ProtectInputs);
        AssertAllZero(protection.ProtectInputs[0]);
    }

    [Fact]
    public async Task Save_clears_plaintext_when_write_fails()
    {
        var dir = TestDirs.New("secret-clear-write");
        var protection = new CapturingProtection();
        var store = new DpapiSecretStore(dir, protection);

        // Force the atomic write to fail: a directory already occupies the tmp path.
        var tmpName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("p1")))[..32] + ".bin.tmp";
        Directory.CreateDirectory(Path.Combine(dir, tmpName));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync("p1", SampleSecrets()));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected error: {error.GetType()}");
        Assert.Single(protection.ProtectInputs);
        AssertAllZero(protection.ProtectInputs[0]);
    }

    [Fact]
    public async Task Save_and_load_success_clear_plaintext_and_keep_values_and_format()
    {
        var dir = TestDirs.New("secret-clear-roundtrip");
        var protection = new CapturingProtection();
        var store = new DpapiSecretStore(dir, protection);

        await store.SaveAsync("p1", SampleSecrets());
        Assert.Single(protection.ProtectInputs);
        AssertAllZero(protection.ProtectInputs[0]);

        // Format is unchanged: the file holds exactly the backend ciphertext bytes.
        var file = Assert.Single(Directory.GetFiles(dir, "*.bin"));
        Assert.Equal(protection.ProtectOutputs[0], await File.ReadAllBytesAsync(file));

        var loaded = await store.LoadAsync("p1");
        Assert.Equal("00000000-0000-0000-0000-000000000001", loaded["uuid"]);
        Assert.Equal("synthetic-marker-395", loaded["note"]);
        Assert.Single(protection.UnprotectOutputs);
        AssertAllZero(protection.UnprotectOutputs[0]);
    }

    [Fact]
    public async Task Load_clears_plaintext_when_deserialization_fails()
    {
        var protection = new CapturingProtection();
        var dir = TestDirs.New("secret-clear-json");
        var store = new DpapiSecretStore(dir, protection);
        await store.SaveAsync("p1", SampleSecrets());

        // Decryption "succeeds" but yields non-JSON plaintext owned by the load operation.
        protection.OnUnprotect = _ => Encoding.UTF8.GetBytes("{invalid-json-395");

        var error = await Assert.ThrowsAsync<SecretAccessException>(() => store.LoadAsync("p1"));
        Assert.Equal("backup.secret.failed", error.LocalizationKey);
        Assert.IsType<JsonException>(error.InnerException);
        Assert.Single(protection.UnprotectOutputs);
        AssertAllZero(protection.UnprotectOutputs[0]);
    }

    [Fact]
    public async Task Aes_tampered_file_fails_closed_without_plaintext_leak()
    {
        var dir = TestDirs.New("secret-clear-aes");
        var store = new AesFileSecretStore(dir);
        await store.SaveAsync("p1", SampleSecrets());

        var file = Assert.Single(Directory.GetFiles(dir, "*.bin"));
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.True(bytes.Length > 32, "Ciphertext must leave room for tampering.");
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(file, bytes);

        // Authentication failure: the partially decrypted buffer is cleared inside
        // Unprotect and the store keeps the #344 fail-closed behavior.
        var error = await Assert.ThrowsAsync<SecretAccessException>(() => store.LoadAsync("p1"));
        Assert.Equal("backup.secret.failed", error.LocalizationKey);
        Assert.IsAssignableFrom<CryptographicException>(error.InnerException);
        Assert.DoesNotContain("synthetic-marker-395", error.ToString(), StringComparison.Ordinal);
    }
}
