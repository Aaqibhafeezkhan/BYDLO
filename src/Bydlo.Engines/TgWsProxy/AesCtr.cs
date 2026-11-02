using System.Numerics;
using System.Security.Cryptography;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>
/// Streaming AES-CTR with a 128-bit big-endian counter (the mode used by MTProto obfuscation).
/// The keystream is produced in batches through ECB for throughput.
/// </summary>
internal sealed class AesCtr : IDisposable
{
    private const int BlockSize = 16;
    private const int BatchBlocks = 256;

    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[BlockSize];
    private readonly byte[] _counterBlocks = new byte[BatchBlocks * BlockSize];
    private readonly byte[] _keystream = new byte[BatchBlocks * BlockSize];
    private int _position;

    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (iv.Length != BlockSize)
        {
            throw new ArgumentException("AES-CTR needs a 16-byte IV.", nameof(iv));
        }

        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        iv.CopyTo(_counter);
        _position = _keystream.Length;
    }

    /// <summary>Encrypts or decrypts in place (CTR is symmetric).</summary>
    public void TransformInPlace(Span<byte> data) => Transform(data, data);

    public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var done = 0;
        while (done < input.Length)
        {
            if (_position == _keystream.Length)
            {
                Refill();
            }

            var n = Math.Min(input.Length - done, _keystream.Length - _position);
            Xor(input.Slice(done, n), _keystream.AsSpan(_position, n), output.Slice(done, n));
            done += n;
            _position += n;
        }
    }

    public byte[] Transform(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        Transform(input, output);
        return output;
    }

    /// <summary>Advances the keystream without producing output.</summary>
    public void Skip(int count) => Transform(new byte[count]);

    public void Dispose() => _aes.Dispose();

    private void Refill()
    {
        for (var block = 0; block < BatchBlocks; block++)
        {
            _counter.CopyTo(_counterBlocks, block * BlockSize);
            for (var i = BlockSize - 1; i >= 0 && ++_counter[i] == 0; i--)
            {
            }
        }

        _aes.EncryptEcb(_counterBlocks, _keystream, PaddingMode.None);
        _position = 0;
    }

    private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= a.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                (new Vector<byte>(a[i..]) ^ new Vector<byte>(b[i..])).CopyTo(destination[i..]);
            }
        }

        for (; i < a.Length; i++)
        {
            destination[i] = (byte)(a[i] ^ b[i]);
        }
    }
}
