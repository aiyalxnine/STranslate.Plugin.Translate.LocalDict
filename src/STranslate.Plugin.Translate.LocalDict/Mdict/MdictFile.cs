using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace STranslate.Plugin.Translate.LocalDict.Mdict;

/// <summary>
/// MDict 词库文件（.mdx 文字词条 / .mdd 资源文件）的只读访问。
/// 构造时只载入关键字索引；词条内容按需读取并解压所在的数据块。
/// 支持引擎 1.2 / 2.0、zlib / LZO / 无压缩，以及仅加密关键字索引的 Encrypted=2。
/// </summary>
public sealed partial class MdictFile : IDisposable
{
    private const int BlockCacheSize = 8;

    private readonly FileStream _stream;
    private readonly object _ioLock = new();
    private readonly int _numberWidth;
    private readonly bool _keyIsUtf16;
    private readonly Encoding _keyEncoding;

    private string[] _keys = [];
    private long[] _keyOffsets = [];
    private long[] _sortedOffsets = [];
    private RecordBlock[] _recordBlocks = [];
    private long _totalRecordSize;
    private long _cursor; // 构造期间顺序解析用的文件位置

    private readonly LinkedList<(int Index, byte[] Data)> _blockCache = new();

    public string FilePath { get; }
    public bool IsResource { get; }
    public string Title { get; }
    public Encoding TextEncoding { get; }
    public IReadOnlyDictionary<string, string> Header { get; }
    public int Count => _keys.Length;

    public MdictFile(string path, CancellationToken cancellationToken = default)
    {
        FilePath = path;
        IsResource = Path.GetExtension(path).Equals(".mdd", StringComparison.OrdinalIgnoreCase);
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);

        try
        {
            Header = ReadHeader();

            var version = double.TryParse(Header.GetValueOrDefault("GeneratedByEngineVersion"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 2.0;
            if (version >= 3.0)
                throw new NotSupportedException($"不支持 MDict {version} 格式的词库");
            _numberWidth = version >= 2.0 ? 8 : 4;

            var encrypted = ParseEncrypted(Header.GetValueOrDefault("Encrypted"));
            if ((encrypted & 1) != 0)
                throw new NotSupportedException("该词库需要注册码解密，暂不支持");

            TextEncoding = IsResource ? Encoding.Unicode : ResolveEncoding(Header.GetValueOrDefault("Encoding"));
            _keyIsUtf16 = TextEncoding.CodePage == Encoding.Unicode.CodePage;
            _keyEncoding = TextEncoding;

            var title = Header.GetValueOrDefault("Title");
            Title = string.IsNullOrWhiteSpace(title) || title.Contains("Title (No HTML code allowed)")
                ? Path.GetFileNameWithoutExtension(path)
                : title.Trim();

            ReadKeySection(version >= 2.0, (encrypted & 2) != 0, cancellationToken);
            ReadRecordSection(cancellationToken);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string GetKey(int index) => _keys[index];

    /// <summary>读取某条记录的原始字节。</summary>
    public byte[] ReadRecord(int index)
    {
        var start = _keyOffsets[index];
        var end = NextOffset(start);
        var length = checked((int)(end - start));
        if (length < 0 || length > 64 * 1024 * 1024)
            throw new InvalidDataException("词条长度异常");

        var result = new byte[length];
        var written = 0;
        var blockIndex = FindBlock(start);
        var position = start;
        while (written < length && blockIndex < _recordBlocks.Length)
        {
            var block = _recordBlocks[blockIndex];
            var data = GetDecompressedBlock(blockIndex);
            var inBlock = (int)(position - block.DecompressedOffset);
            var take = Math.Min(length - written, data.Length - inBlock);
            data.AsSpan(inBlock, take).CopyTo(result.AsSpan(written));
            written += take;
            position += take;
            blockIndex++;
        }
        return result;
    }

    /// <summary>读取文字词条（mdx）。</summary>
    public string ReadText(int index) => TextEncoding.GetString(ReadRecord(index)).TrimEnd('\0', '\r', '\n', ' ');

    public void Dispose()
    {
        lock (_ioLock)
        {
            _stream.Dispose();
            _blockCache.Clear();
        }
    }

    #region 头部

    private Dictionary<string, string> ReadHeader()
    {
        var lengthBytes = ReadExactly(0, 4);
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (headerLength <= 0 || headerLength > 16 * 1024 * 1024)
            throw new InvalidDataException("不是有效的 MDict 文件");

        var headerBytes = ReadExactly(4, headerLength);
        _cursor = 4 + headerLength + 4; // 跳过 adler32
        var xml = Encoding.Unicode.GetString(headerBytes).TrimEnd('\0');
        if (!xml.StartsWith("<Dictionary", StringComparison.Ordinal) && !xml.StartsWith("<Library_Data", StringComparison.Ordinal))
            throw new InvalidDataException("不是有效的 MDict 文件");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HeaderAttributeRegex().Matches(xml))
            result[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Value);
        return result;
    }

    private static int ParseEncrypted(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("No", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase))
            return 1;
        return int.TryParse(value, out var n) ? n : 0;
    }

    private static Encoding ResolveEncoding(string? name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var normalized = (name ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "" or "UTF-8" or "UTF8" => new UTF8Encoding(false),
            "UTF-16" or "UTF16" or "UNICODE" => Encoding.Unicode,
            "GBK" or "GB2312" or "GB18030" => Encoding.GetEncoding("GB18030"),
            "BIG5" or "BIG-5" => Encoding.GetEncoding("big5"),
            _ => Encoding.GetEncoding(name!)
        };
    }

    [GeneratedRegex("""(\w+)="(.*?)"(?=\s|/?>)""", RegexOptions.Singleline)]
    private static partial Regex HeaderAttributeRegex();

    #endregion

    #region 关键字

    private void ReadKeySection(bool v2, bool keyInfoEncrypted, CancellationToken cancellationToken)
    {
        var headerSize = v2 ? 5 * 8 : 4 * 4;
        var header = ReadExactly(_cursor, headerSize);
        var r = new BigEndianReader(header, _numberWidth);
        var numKeyBlocks = r.ReadNumber();
        var numEntries = r.ReadNumber();
        if (v2)
            r.ReadNumber(); // 索引解压后大小
        var keyInfoSize = r.ReadNumber();
        var keyBlocksSize = r.ReadNumber();
        _cursor += headerSize + (v2 ? 4 : 0);

        var keyInfoRaw = ReadExactly(_cursor, checked((int)keyInfoSize));
        _cursor += keyInfoSize;
        var keyBlocksStart = _cursor;
        _cursor += keyBlocksSize;

        byte[] keyInfo;
        if (v2)
        {
            if (keyInfoEncrypted)
                DecryptKeyInfo(keyInfoRaw);
            keyInfo = DecompressBlock(keyInfoRaw, -1);
        }
        else
        {
            keyInfo = keyInfoRaw;
        }

        var blockSizes = ParseKeyBlockInfo(keyInfo, (int)numKeyBlocks, v2);

        var keys = new List<string>((int)numEntries);
        var offsets = new List<long>((int)numEntries);
        var position = keyBlocksStart;
        foreach (var (compressed, decompressed) in blockSizes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = ReadExactly(position, checked((int)compressed));
            position += compressed;
            var data = DecompressBlock(raw, checked((int)decompressed));
            ParseKeyBlock(data, keys, offsets);
        }

        _keys = [.. keys];
        _keyOffsets = [.. offsets];
        _sortedOffsets = [.. offsets.Distinct().Order()];
    }

    private static void DecryptKeyInfo(byte[] block)
    {
        Span<byte> seed = stackalloc byte[8];
        block.AsSpan(4, 4).CopyTo(seed);
        BinaryPrimitives.WriteUInt32LittleEndian(seed[4..], 0x3695);
        var key = Ripemd128.Hash(seed);

        byte previous = 0x36;
        for (var i = 8; i < block.Length; i++)
        {
            var b = block[i];
            var t = (byte)((b >> 4) | (b << 4));
            block[i] = (byte)(t ^ previous ^ ((i - 8) & 0xFF) ^ key[(i - 8) % key.Length]);
            previous = b;
        }
    }

    private List<(long Compressed, long Decompressed)> ParseKeyBlockInfo(byte[] info, int numBlocks, bool v2)
    {
        var r = new BigEndianReader(info, _numberWidth);
        var unit = _keyIsUtf16 ? 2 : 1;
        var terminator = v2 ? 1 : 0;
        var result = new List<(long, long)>(numBlocks);
        for (var i = 0; i < numBlocks; i++)
        {
            r.ReadNumber(); // 本块词条数
            var headSize = v2 ? r.ReadUInt16() : r.ReadByte();
            r.Skip((headSize + terminator) * unit);
            var tailSize = v2 ? r.ReadUInt16() : r.ReadByte();
            r.Skip((tailSize + terminator) * unit);
            result.Add((r.ReadNumber(), r.ReadNumber()));
        }
        return result;
    }

    private void ParseKeyBlock(byte[] data, List<string> keys, List<long> offsets)
    {
        var pos = 0;
        while (pos + _numberWidth <= data.Length)
        {
            var offset = _numberWidth == 8
                ? BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += _numberWidth;

            int end;
            if (_keyIsUtf16)
            {
                end = pos;
                while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
                    end += 2;
            }
            else
            {
                end = Array.IndexOf(data, (byte)0, pos);
                if (end < 0) end = data.Length;
            }

            keys.Add(_keyEncoding.GetString(data, pos, end - pos));
            offsets.Add(offset);
            pos = end + (_keyIsUtf16 ? 2 : 1);
        }
    }

    #endregion

    #region 记录

    private void ReadRecordSection(CancellationToken cancellationToken)
    {
        var headerSize = 4 * _numberWidth;
        var r = new BigEndianReader(ReadExactly(_cursor, headerSize), _numberWidth);
        var numBlocks = r.ReadNumber();
        r.ReadNumber(); // 词条总数
        var infoSize = r.ReadNumber();
        r.ReadNumber(); // 数据区大小
        _cursor += headerSize;

        var info = new BigEndianReader(ReadExactly(_cursor, checked((int)infoSize)), _numberWidth);
        _cursor += infoSize;

        var blocks = new RecordBlock[numBlocks];
        long fileOffset = _cursor, decompressedOffset = 0;
        for (var i = 0; i < numBlocks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compressed = info.ReadNumber();
            var decompressed = info.ReadNumber();
            blocks[i] = new RecordBlock(fileOffset, compressed, decompressedOffset, decompressed);
            fileOffset += compressed;
            decompressedOffset += decompressed;
        }
        _recordBlocks = blocks;
        _totalRecordSize = decompressedOffset;
    }

    private long NextOffset(long start)
    {
        var i = Array.BinarySearch(_sortedOffsets, start);
        i = i < 0 ? ~i : i + 1;
        return i < _sortedOffsets.Length ? _sortedOffsets[i] : _totalRecordSize;
    }

    private int FindBlock(long offset)
    {
        int lo = 0, hi = _recordBlocks.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var block = _recordBlocks[mid];
            if (offset < block.DecompressedOffset) hi = mid - 1;
            else if (offset >= block.DecompressedOffset + block.DecompressedSize) lo = mid + 1;
            else return mid;
        }
        throw new InvalidDataException("词条偏移超出数据范围");
    }

    private byte[] GetDecompressedBlock(int index)
    {
        lock (_ioLock)
        {
            for (var node = _blockCache.First; node != null; node = node.Next)
            {
                if (node.Value.Index != index) continue;
                _blockCache.Remove(node);
                _blockCache.AddFirst(node);
                return node.Value.Data;
            }

            var block = _recordBlocks[index];
            var raw = ReadExactly(block.FileOffset, checked((int)block.CompressedSize));
            var data = DecompressBlock(raw, checked((int)block.DecompressedSize));
            _blockCache.AddFirst((index, data));
            if (_blockCache.Count > BlockCacheSize)
                _blockCache.RemoveLast();
            return data;
        }
    }

    private readonly record struct RecordBlock(long FileOffset, long CompressedSize, long DecompressedOffset, long DecompressedSize);

    #endregion

    #region 底层读取

    /// <param name="raw">4 字节压缩类型 + 4 字节校验 + 数据</param>
    /// <param name="decompressedSize">已知解压大小；未知时为 -1（仅 zlib）</param>
    private static byte[] DecompressBlock(byte[] raw, int decompressedSize)
    {
        if (raw.Length < 8)
            throw new InvalidDataException("数据块过短");
        var type = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var payload = raw.AsSpan(8);
        switch (type)
        {
            case 0:
                return payload.ToArray();
            case 1:
                if (decompressedSize < 0)
                    throw new InvalidDataException("LZO 数据块缺少解压大小");
                return Lzo1x.Decompress(payload, decompressedSize);
            case 2:
                using (var input = new MemoryStream(raw, 8, raw.Length - 8, false))
                using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
                using (var output = decompressedSize > 0 ? new MemoryStream(decompressedSize) : new MemoryStream())
                {
                    zlib.CopyTo(output);
                    return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
                }
            default:
                throw new InvalidDataException($"未知的压缩类型 {type}");
        }
    }

    private byte[] ReadExactly(long position, int count)
    {
        var buffer = new byte[count];
        lock (_ioLock)
        {
            _stream.Seek(position, SeekOrigin.Begin);
            _stream.ReadExactly(buffer);
        }
        return buffer;
    }

    private ref struct BigEndianReader(ReadOnlySpan<byte> data, int numberWidth)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly int _width = numberWidth;
        private int _pos;

        public long ReadNumber()
        {
            long value = _width == 8
                ? BinaryPrimitives.ReadInt64BigEndian(_data[_pos..])
                : BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
            _pos += _width;
            return value;
        }

        public int ReadUInt16()
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(_data[_pos..]);
            _pos += 2;
            return value;
        }

        public int ReadByte() => _data[_pos++];

        public void Skip(int count) => _pos += count;
    }

    #endregion
}
