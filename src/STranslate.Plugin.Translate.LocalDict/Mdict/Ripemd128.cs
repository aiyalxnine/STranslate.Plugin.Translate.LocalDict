namespace STranslate.Plugin.Translate.LocalDict.Mdict;

/// <summary>
/// RIPEMD-128 摘要，仅用于解开 MDX 的关键字索引加密（Encrypted=2）。
/// </summary>
internal static class Ripemd128
{
    private static readonly int[] R =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
        3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
        1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2
    ];

    private static readonly int[] RP =
    [
        5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
        6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
        15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
        8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14
    ];

    private static readonly int[] S =
    [
        11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
        7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
        11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
        11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12
    ];

    private static readonly int[] SP =
    [
        8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
        9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
        9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
        15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8
    ];

    private static readonly uint[] K = [0x00000000, 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC];
    private static readonly uint[] KP = [0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x00000000];

    public static byte[] Hash(ReadOnlySpan<byte> message)
    {
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476;

        var paddedLength = (message.Length + 8) / 64 * 64 + 64;
        var data = new byte[paddedLength];
        message.CopyTo(data);
        data[message.Length] = 0x80;
        BitConverter.TryWriteBytes(data.AsSpan(paddedLength - 8), (ulong)message.Length * 8);

        var x = new uint[16];
        for (var offset = 0; offset < paddedLength; offset += 64)
        {
            for (var i = 0; i < 16; i++)
                x[i] = BitConverter.ToUInt32(data, offset + i * 4);

            uint a = h0, b = h1, c = h2, d = h3;
            uint ap = h0, bp = h1, cp = h2, dp = h3;

            for (var j = 0; j < 64; j++)
            {
                var round = j >> 4;
                var t = RotateLeft(a + F(round, b, c, d) + x[R[j]] + K[round], S[j]);
                a = d; d = c; c = b; b = t;

                t = RotateLeft(ap + F(3 - round, bp, cp, dp) + x[RP[j]] + KP[round], SP[j]);
                ap = dp; dp = cp; cp = bp; bp = t;
            }

            var tmp = h1 + c + dp;
            h1 = h2 + d + ap;
            h2 = h3 + a + bp;
            h3 = h0 + b + cp;
            h0 = tmp;
        }

        var result = new byte[16];
        BitConverter.TryWriteBytes(result.AsSpan(0), h0);
        BitConverter.TryWriteBytes(result.AsSpan(4), h1);
        BitConverter.TryWriteBytes(result.AsSpan(8), h2);
        BitConverter.TryWriteBytes(result.AsSpan(12), h3);
        return result;
    }

    private static uint F(int round, uint x, uint y, uint z) => round switch
    {
        0 => x ^ y ^ z,
        1 => (x & y) | (~x & z),
        2 => (x | ~y) ^ z,
        _ => (x & z) | (y & ~z)
    };

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}
