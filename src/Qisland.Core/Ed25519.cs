using System.Numerics;
using System.Security.Cryptography;

namespace Qisland.Core;

/// <summary>
/// 零依赖的 Ed25519（RFC 8032 §5.1）。.NET 8 的 System.Security.Cryptography 没有内置 Ed25519，
/// 这里照 RFC 8032 第 6 节的参考实现用 BigInteger 写了一份：扩展坐标点加、倍点乘、点压缩 / 解压。
/// · Verify：插件市场验 registry.json / revoked.json 的签名，拒绝非规范的 S（S ≥ L）和解不出来的点。
/// · Sign：只给自检和维护者签索引用（不是常数时间实现，不要拿去处理在线私钥）。
/// 自检里用 RFC 8032 7.1 节的官方测试向量逐字节核对。
/// </summary>
public static class Ed25519
{
    public const int PublicKeySize = 32, SignatureSize = 64, SeedSize = 32;

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point G = MakeBase();

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static BigInteger Mod(BigInteger a) { var r = a % P; return r.Sign < 0 ? r + P : r; }
    private static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static Point MakeBase()
    {
        var y = Mod(4 * Inv(5));
        var x = RecoverX(y, 0) ?? throw new InvalidOperationException("Ed25519 base point");
        return new Point(x, y, 1, Mod(x * y));
    }

    private static Point Add(Point a, Point b)
    {
        var A = Mod((a.Y - a.X) * (b.Y - b.X));
        var B = Mod((a.Y + a.X) * (b.Y + b.X));
        var C = Mod(a.T * 2 * D * b.T);
        var Dd = Mod(a.Z * 2 * b.Z);
        BigInteger E = B - A, F = Dd - C, Gg = Dd + C, H = B + A;
        return new Point(Mod(E * F), Mod(Gg * H), Mod(F * Gg), Mod(E * H));
    }

    private static Point Mul(BigInteger s, Point p)
    {
        var q = new Point(0, 1, 1, 0);   // 单位元
        while (s > 0)
        {
            if (!s.IsEven) q = Add(q, p);
            p = Add(p, p);
            s >>= 1;
        }
        return q;
    }

    private static bool Equal(Point a, Point b) =>
        Mod(a.X * b.Z - b.X * a.Z).IsZero && Mod(a.Y * b.Z - b.Y * a.Z).IsZero;

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) return null;
        var x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2.IsZero) return sign != 0 ? null : BigInteger.Zero;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtM1);
        if (!Mod(x * x - x2).IsZero) return null;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    private static BigInteger Le(ReadOnlySpan<byte> b) => new(b, isUnsigned: true, isBigEndian: false);

    private static byte[] Bytes32(BigInteger v)
    {
        var raw = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        var r = new byte[32];
        Array.Copy(raw, r, Math.Min(raw.Length, 32));
        return r;
    }

    private static byte[] Compress(Point p)
    {
        var zi = Inv(p.Z);
        var x = Mod(p.X * zi); var y = Mod(p.Y * zi);
        var r = Bytes32(y);
        if (!x.IsEven) r[31] |= 0x80;
        return r;
    }

    private static Point? Decompress(ReadOnlySpan<byte> s)
    {
        if (s.Length != 32) return null;
        var buf = s.ToArray();
        var sign = buf[31] >> 7;
        buf[31] &= 0x7F;
        var y = Le(buf);
        if (RecoverX(y, sign) is not { } x) return null;
        return new Point(x, y, 1, Mod(x * y));
    }

    private static BigInteger HashModL(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts) h.AppendData(p);
        return Le(h.GetHashAndReset()) % L;
    }

    private static (BigInteger a, byte[] prefix) Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize) throw new ArgumentException("Ed25519 私钥种子必须是 32 字节");
        var h = SHA512.HashData(seed);
        var a = h.AsSpan(0, 32).ToArray();
        a[0] &= 248; a[31] &= 127; a[31] |= 64;
        return (Le(a), h[32..]);
    }

    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed) => Compress(Mul(Expand(seed).a, G));

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (a, prefix) = Expand(seed);
        var pub = Compress(Mul(a, G));
        var msg = message.ToArray();
        var r = HashModL(prefix, msg);
        var R = Compress(Mul(r, G));
        var k = HashModL(R, pub, msg);
        var s = (r + k * a) % L;
        return [.. R, .. Bytes32(s)];
    }

    /// <summary>验签：公钥 32 字节、签名 64 字节；任何格式问题都只返回 false，不抛异常。</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        try
        {
            if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize) return false;
            if (Decompress(publicKey) is not { } A) return false;
            var rBytes = signature[..32].ToArray();
            if (Decompress(rBytes) is not { } R) return false;
            var s = Le(signature[32..]);
            if (s >= L) return false;   // 非规范签名（可延展）一律拒绝
            var k = HashModL(rBytes, publicKey.ToArray(), message.ToArray());
            return Equal(Mul(s, G), Add(R, Mul(k, A)));
        }
        catch { return false; }
    }
}
