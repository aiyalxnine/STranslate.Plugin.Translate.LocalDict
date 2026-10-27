namespace STranslate.Plugin.Translate.LocalDict.Mdict;

/// <summary>
/// LZO1X 解压，旧版 MDX 的数据块会用到。按 LZO1X 压缩格式编写，
/// 解码流程与参考实现 lzo1x_decompress_safe 相同，另加了回溯越界检查。
/// </summary>
internal static class Lzo1x
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, int decompressedSize)
    {
        var dst = new byte[decompressedSize];
        int ip = 0, op = 0, t, mPos;

        if (src[ip] > 17)
        {
            t = src[ip++] - 17;
            if (t < 4)
                goto MatchNext;
            CopyLiterals(src, ref ip, dst, ref op, t);
            goto FirstLiteralRun;
        }

    Loop:
        t = src[ip++];
        if (t >= 16)
            goto Match;
        if (t == 0)
        {
            while (src[ip] == 0)
            {
                t += 255;
                ip++;
            }
            t += 15 + src[ip++];
        }
        CopyLiterals(src, ref ip, dst, ref op, t + 3);

    FirstLiteralRun:
        t = src[ip++];
        if (t >= 16)
            goto Match;
        mPos = op - (1 + 0x0800) - (t >> 2) - (src[ip++] << 2);
        CopyMatch(dst, ref op, mPos, 3);
        goto MatchDone;

    Match:
        if (t >= 64)
        {
            mPos = op - 1 - ((t >> 2) & 7) - (src[ip++] << 3);
            t = (t >> 5) - 1;
            CopyMatch(dst, ref op, mPos, t + 2);
            goto MatchDone;
        }
        if (t >= 32)
        {
            t &= 31;
            if (t == 0)
            {
                while (src[ip] == 0)
                {
                    t += 255;
                    ip++;
                }
                t += 31 + src[ip++];
            }
            mPos = op - 1 - ((src[ip] >> 2) + (src[ip + 1] << 6));
            ip += 2;
        }
        else if (t >= 16)
        {
            mPos = op - ((t & 8) << 11);
            t &= 7;
            if (t == 0)
            {
                while (src[ip] == 0)
                {
                    t += 255;
                    ip++;
                }
                t += 7 + src[ip++];
            }
            mPos -= (src[ip] >> 2) + (src[ip + 1] << 6);
            ip += 2;
            if (mPos == op)
                return op == dst.Length ? dst : dst[..op];
            mPos -= 0x4000;
        }
        else
        {
            mPos = op - 1 - (t >> 2) - (src[ip++] << 2);
            CopyMatch(dst, ref op, mPos, 2);
            goto MatchDone;
        }
        CopyMatch(dst, ref op, mPos, t + 2);

    MatchDone:
        t = src[ip - 2] & 3;
        if (t == 0)
            goto Loop;

    MatchNext:
        CopyLiterals(src, ref ip, dst, ref op, t);
        t = src[ip++];
        goto Match;
    }

    private static void CopyLiterals(ReadOnlySpan<byte> src, ref int ip, byte[] dst, ref int op, int count)
    {
        src.Slice(ip, count).CopyTo(dst.AsSpan(op, count));
        ip += count;
        op += count;
    }

    // 匹配区可能与输出重叠，必须逐字节复制
    private static void CopyMatch(byte[] dst, ref int op, int mPos, int count)
    {
        if (mPos < 0)
            throw new InvalidDataException("LZO 数据损坏：回溯位置越界");
        for (var i = 0; i < count; i++)
            dst[op++] = dst[mPos++];
    }
}
