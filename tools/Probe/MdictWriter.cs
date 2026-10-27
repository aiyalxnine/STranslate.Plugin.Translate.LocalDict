using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using STranslate.Plugin.Translate.LocalDict.Mdict;

/// <summary>
/// 生成测试用的 MDict 2.0 文件（.mdx / .mdd），让自动测试不依赖任何真实词库。
/// 支持 zlib / LZO（仅字面量编码）/ 不压缩的数据块、Encrypted=2 的索引加密、UTF-8 / UTF-16 键。
/// </summary>
internal static class MdictWriter
{
    public enum BlockCompression { Zlib, LzoLiteral, None }

    public static void Write(string path, IEnumerable<(string Key, byte[] Record)> entries, bool resource,
        string encoding = "UTF-8", bool encryptKeyInfo = true,
        BlockCompression recordCompression = BlockCompression.Zlib, int entriesPerBlock = 3, string title = "Test")
    {
        var utf16 = resource || encoding.Equals("UTF-16", StringComparison.OrdinalIgnoreCase);
        encryptKeyInfo &= !resource; // MDD 头部写的是 Encrypted="0"
        Encoding keyEncoding = utf16 ? Encoding.Unicode : new UTF8Encoding(false);
        var unit = utf16 ? 2 : 1;
        var list = entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();

        // 记录区：按词条顺序拼接，块边界与词条边界对齐
        var offsets = new long[list.Count];
        long position = 0;
        for (var i = 0; i < list.Count; i++)
        {
            offsets[i] = position;
            position += list[i].Record.Length;
        }

        var keyBlocks = new List<byte[]>();
        var keyInfo = new MemoryStream();
        var recordBlocks = new List<(byte[] Raw, long Decompressed)>();
        for (var start = 0; start < list.Count; start += entriesPerBlock)
        {
            var chunk = Enumerable.Range(start, Math.Min(entriesPerBlock, list.Count - start)).ToList();

            var keyData = new MemoryStream();
            foreach (var i in chunk)
            {
                WriteUInt64(keyData, (ulong)offsets[i]);
                keyData.Write(keyEncoding.GetBytes(list[i].Key));
                keyData.Write(new byte[unit]);
            }
            var keyBlock = Compress(keyData.ToArray(), BlockCompression.Zlib);
            keyBlocks.Add(keyBlock);

            WriteUInt64(keyInfo, (ulong)chunk.Count);
            WriteBoundaryKey(keyInfo, keyEncoding.GetBytes(list[chunk[0]].Key), unit);
            WriteBoundaryKey(keyInfo, keyEncoding.GetBytes(list[chunk[^1]].Key), unit);
            WriteUInt64(keyInfo, (ulong)keyBlock.Length);
            WriteUInt64(keyInfo, (ulong)keyData.Length);

            var recordData = chunk.SelectMany(i => list[i].Record).ToArray();
            recordBlocks.Add((Compress(recordData, recordCompression), recordData.Length));
        }

        var keyInfoPlain = keyInfo.ToArray();
        var keyInfoRaw = Compress(keyInfoPlain, BlockCompression.Zlib);
        if (encryptKeyInfo)
            EncryptKeyInfo(keyInfoRaw);

        using var output = File.Create(path);

        var header = resource
            ? $"""<Library_Data GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encrypted="0" Encoding="" Format="" KeyCaseSensitive="No" Title="{title}"/>"""
            : $"""<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encrypted="{(encryptKeyInfo ? 2 : 0)}" Encoding="{encoding}" Format="Html" StripKey="Yes" KeyCaseSensitive="No" Title="{title}" Description="test"/>""";
        var headerBytes = Encoding.Unicode.GetBytes(header + "\r\n\0");
        WriteUInt32BigEndian(output, (uint)headerBytes.Length);
        output.Write(headerBytes);
        WriteUInt32LittleEndian(output, Adler32(headerBytes));

        var keyHeader = new MemoryStream();
        WriteUInt64(keyHeader, (ulong)keyBlocks.Count);
        WriteUInt64(keyHeader, (ulong)list.Count);
        WriteUInt64(keyHeader, (ulong)keyInfoPlain.Length);
        WriteUInt64(keyHeader, (ulong)keyInfoRaw.Length);
        WriteUInt64(keyHeader, (ulong)keyBlocks.Sum(b => b.Length));
        output.Write(keyHeader.ToArray());
        WriteUInt32BigEndian(output, Adler32(keyHeader.ToArray()));
        output.Write(keyInfoRaw);
        keyBlocks.ForEach(b => output.Write(b));

        WriteUInt64(output, (ulong)recordBlocks.Count);
        WriteUInt64(output, (ulong)list.Count);
        WriteUInt64(output, (ulong)(recordBlocks.Count * 16));
        WriteUInt64(output, (ulong)recordBlocks.Sum(b => b.Raw.Length));
        foreach (var (raw, decompressed) in recordBlocks)
        {
            WriteUInt64(output, (ulong)raw.Length);
            WriteUInt64(output, (ulong)decompressed);
        }
        recordBlocks.ForEach(b => output.Write(b.Raw));
    }

    private static void WriteBoundaryKey(Stream s, byte[] key, int unit)
    {
        Span<byte> size = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)(key.Length / unit));
        s.Write(size);
        s.Write(key);
        s.Write(new byte[unit]);
    }

    /// <summary>4 字节压缩类型（小端）+ 4 字节 adler32（大端）+ 数据</summary>
    private static byte[] Compress(byte[] data, BlockCompression compression)
    {
        var block = new MemoryStream();
        WriteUInt32LittleEndian(block, compression switch { BlockCompression.None => 0u, BlockCompression.LzoLiteral => 1u, _ => 2u });
        WriteUInt32BigEndian(block, Adler32(data));
        switch (compression)
        {
            case BlockCompression.None:
                block.Write(data);
                break;
            case BlockCompression.LzoLiteral:
                block.Write(LzoLiteralStream(data));
                break;
            default:
                using (var zlib = new ZLibStream(block, CompressionLevel.Optimal, leaveOpen: true))
                    zlib.Write(data);
                break;
        }
        return block.ToArray();
    }

    /// <summary>只含一段字面量的合法 LZO1X 流（长度 ≥ 4），以 0x11 0x00 0x00 结束。</summary>
    private static byte[] LzoLiteralStream(byte[] data)
    {
        if (data.Length < 4)
            throw new ArgumentException("LZO 字面量段至少 4 字节");
        var s = new MemoryStream();
        if (data.Length <= 238)
        {
            s.WriteByte((byte)(17 + data.Length));
        }
        else
        {
            s.WriteByte(0);
            var rest = data.Length - 18;
            while (rest > 255)
            {
                s.WriteByte(0);
                rest -= 255;
            }
            s.WriteByte((byte)rest);
        }
        s.Write(data);
        s.Write([0x11, 0x00, 0x00]);
        return s.ToArray();
    }

    /// <summary>DecryptKeyInfo 的逆运算。</summary>
    private static void EncryptKeyInfo(byte[] block)
    {
        var seed = new byte[8];
        block.AsSpan(4, 4).CopyTo(seed);
        BinaryPrimitives.WriteUInt32LittleEndian(seed.AsSpan(4), 0x3695);
        var key = Ripemd128.Hash(seed);
        byte previous = 0x36;
        for (var i = 8; i < block.Length; i++)
        {
            var x = (byte)(block[i] ^ previous ^ ((i - 8) & 0xFF) ^ key[(i - 8) % key.Length]);
            var b = (byte)((x >> 4) | (x << 4));
            block[i] = b;
            previous = b;
        }
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static void WriteUInt64(Stream s, ulong v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteUInt32BigEndian(Stream s, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteUInt32LittleEndian(Stream s, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
        s.Write(buf);
    }
}
