namespace Bydlo.Core.Warp;

/// <summary>
/// X25519 key agreement (RFC 7748) over Curve25519, Montgomery ladder.
/// Self-contained port of TweetNaCl's <c>crypto_scalarmult</c> (public domain,
/// http://tweetnacl.cr.yp.to), matching the key generation used by wgcf
/// (MIT, https://github.com/ViRb3/wgcf). No code is copied from either project;
/// only the published arithmetic is reimplemented so BYDLO needs no extra dependency.
/// </summary>
internal static class X25519
{
    private const int FieldSize = 16;

    private static readonly long[] Scalar121665 = [0xDB41, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static readonly byte[] BasePoint =
        [9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] GeneratePublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != 32)
        {
            throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateKey));
        }

        var clamped = privateKey.ToArray();
        clamped[31] = (byte)((clamped[31] & 127) | 64);
        clamped[0] &= 248;
        return ScalarMult(clamped, BasePoint);
    }

    public static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        if (scalar.Length != 32 || point.Length != 32)
        {
            throw new ArgumentException("X25519 scalar and point must be 32 bytes each.");
        }

        var z = scalar.ToArray();
        z[31] = (byte)((z[31] & 127) | 64);
        z[0] &= 248;

        var x = new long[80];
        var u = new long[FieldSize];
        Unpack(u, point);
        Array.Copy(u, x, FieldSize);

        var a = new long[FieldSize];
        var b = new long[FieldSize];
        var c = new long[FieldSize];
        var d = new long[FieldSize];
        var e = new long[FieldSize];
        var f = new long[FieldSize];
        for (var i = 0; i < FieldSize; i++)
        {
            b[i] = x[i];
            d[i] = a[i] = c[i] = 0;
        }

        a[0] = d[0] = 1;
        for (var i = 254; i >= 0; --i)
        {
            var r = (z[i >> 3] >> (i & 7)) & 1;
            Sel(a, b, r);
            Sel(c, d, r);
            Add(e, a, c);
            Sub(a, a, c);
            Add(c, b, d);
            Sub(b, b, d);
            Square(d, e);
            Square(f, a);
            Mult(a, c, a);
            Mult(c, b, e);
            Add(e, a, c);
            Sub(a, a, c);
            Square(b, a);
            Sub(c, d, f);
            Mult(a, c, Scalar121665);
            Add(a, a, d);
            Mult(c, c, a);
            Mult(a, d, f);
            Mult(d, b, x);
            Square(b, e);
            Sel(a, b, r);
            Sel(c, d, r);
        }

        var a16 = new Span<long>(x, 16, FieldSize);
        var c32 = new Span<long>(x, 32, FieldSize);
        a.CopyTo(a16);
        c.CopyTo(c32);
        new Span<long>(b, 0, FieldSize).CopyTo(new Span<long>(x, 48, FieldSize));
        new Span<long>(d, 0, FieldSize).CopyTo(new Span<long>(x, 64, FieldSize));

        var inv = new long[FieldSize];
        Inv(inv, c32.ToArray());
        inv.CopyTo(c32);
        var m = new long[FieldSize];
        Mult(m, a16.ToArray(), c32.ToArray());
        m.CopyTo(a16);

        var q = new byte[32];
        Pack(q, a16.ToArray());
        return q;
    }

    private static void Carry(long[] o)
    {
        for (var i = 0; i < 16; i++)
        {
            o[i] += 1L << 16;
            var c = o[i] >> 16;
            o[(i + 1) * (i < 15 ? 1 : 0)] += c - 1 + (i == 15 ? 37 * (c - 1) : 0);
            o[i] -= c << 16;
        }
    }

    private static void Sel(long[] p, long[] q, int b)
    {
        var c = ~(b - 1);
        for (var i = 0; i < 16; i++)
        {
            var t = c & (p[i] ^ q[i]);
            p[i] ^= t;
            q[i] ^= t;
        }
    }

    private static void Pack(byte[] o, long[] n)
    {
        var t = new long[16];
        var m = new long[16];
        Array.Copy(n, t, 16);
        Carry(t);
        Carry(t);
        Carry(t);
        for (var j = 0; j < 2; j++)
        {
            m[0] = t[0] - 0xffed;
            for (var i = 1; i < 15; i++)
            {
                m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
                m[i - 1] &= 0xffff;
            }

            m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
            var b = (int)((m[15] >> 16) & 1);
            m[14] &= 0xffff;
            Sel(t, m, 1 - b);
        }

        for (var i = 0; i < 16; i++)
        {
            o[2 * i] = (byte)(t[i] & 0xff);
            o[2 * i + 1] = (byte)(t[i] >> 8);
        }
    }

    private static void Unpack(long[] o, ReadOnlySpan<byte> n)
    {
        for (var i = 0; i < 16; i++)
        {
            o[i] = n[2 * i] + ((long)n[2 * i + 1] << 8);
        }

        o[15] &= 0x7fff;
    }

    private static void Add(long[] o, long[] a, long[] b)
    {
        for (var i = 0; i < 16; i++)
        {
            o[i] = a[i] + b[i];
        }
    }

    private static void Sub(long[] o, long[] a, long[] b)
    {
        for (var i = 0; i < 16; i++)
        {
            o[i] = a[i] - b[i];
        }
    }

    private static void Mult(long[] o, long[] a, long[] b)
    {
        var t = new long[31];
        for (var i = 0; i < 16; i++)
        {
            for (var j = 0; j < 16; j++)
            {
                t[i + j] += a[i] * b[j];
            }
        }

        for (var i = 0; i < 15; i++)
        {
            t[i] += t[i + 16] * 38;
        }

        for (var i = 0; i < 16; i++)
        {
            o[i] = t[i];
        }

        Carry(o);
        Carry(o);
    }

    private static void Square(long[] o, long[] a) => Mult(o, a, a);

    private static void Inv(long[] o, long[] i)
    {
        var c = new long[16];
        Array.Copy(i, c, 16);
        for (var a = 253; a >= 0; a--)
        {
            Square(c, c);
            if (a != 2 && a != 4)
            {
                Mult(c, c, i);
            }
        }

        Array.Copy(c, o, 16);
    }
}
