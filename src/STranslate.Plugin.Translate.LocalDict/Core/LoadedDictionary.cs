using System.Security.Cryptography;
using System.Text;
using STranslate.Plugin.Translate.LocalDict.Mdict;

namespace STranslate.Plugin.Translate.LocalDict.Core;

/// <summary>
/// 一本已打开的词库：MDX 词条 + 同名 MDD 资源（发音、图片）及其查找索引。
/// </summary>
public sealed class LoadedDictionary : IDisposable
{
    private const string LinkPrefix = "@@@LINK=";
    private const int MaxLinkDepth = 6;

    private readonly MdictFile _mdx;
    private readonly List<MdictFile> _resources;
    private readonly KeyIndex _index;
    private readonly KeyIndex? _strippedIndex;
    private readonly List<Dictionary<string, int>> _resourceIndex;

    /// <summary>由文件路径得到的稳定标识，用于发音地址。</summary>
    public string Id { get; }
    public string MdxPath { get; }
    public string Title => _mdx.Title;
    public int EntryCount => _mdx.Count;
    public int ResourceCount => _resourceIndex.Sum(r => r.Count);

    private LoadedDictionary(string path, MdictFile mdx, List<MdictFile> resources, CancellationToken cancellationToken)
    {
        MdxPath = path;
        Id = StableId(path);
        _mdx = mdx;
        _resources = resources;

        _index = new KeyIndex(mdx.Count, StringComparer.OrdinalIgnoreCase);
        var strip = !"No".Equals(mdx.Header.GetValueOrDefault("StripKey"), StringComparison.OrdinalIgnoreCase);
        _strippedIndex = strip ? new KeyIndex(mdx.Count, StringComparer.Ordinal) : null;
        for (var i = 0; i < mdx.Count; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var key = mdx.GetKey(i).Trim();
            _index.Add(key, i);
            if (_strippedIndex != null)
            {
                var stripped = StripKey(key);
                if (stripped.Length > 0) _strippedIndex.Add(stripped, i);
            }
        }

        _resourceIndex = [];
        foreach (var res in resources)
        {
            var map = new Dictionary<string, int>(res.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < res.Count; i++)
                map.TryAdd(NormalizeResourcePath(res.GetKey(i)), i);
            _resourceIndex.Add(map);
        }
    }

    public static LoadedDictionary Open(string mdxPath, CancellationToken cancellationToken = default)
    {
        var mdx = new MdictFile(mdxPath, cancellationToken);
        var resources = new List<MdictFile>();
        try
        {
            foreach (var mddPath in FindResourceFiles(mdxPath))
                resources.Add(new MdictFile(mddPath, cancellationToken));
            return new LoadedDictionary(mdxPath, mdx, resources, cancellationToken);
        }
        catch
        {
            mdx.Dispose();
            resources.ForEach(r => r.Dispose());
            throw;
        }
    }

    /// <summary>与 MDX 同名的 .mdd，以及分卷 .1.mdd、.2.mdd …</summary>
    public static IEnumerable<string> FindResourceFiles(string mdxPath)
    {
        var basePath = Path.Combine(Path.GetDirectoryName(mdxPath) ?? string.Empty, Path.GetFileNameWithoutExtension(mdxPath));
        if (File.Exists(basePath + ".mdd"))
            yield return basePath + ".mdd";
        for (var i = 1; i < 50 && File.Exists($"{basePath}.{i}.mdd"); i++)
            yield return $"{basePath}.{i}.mdd";
    }

    /// <summary>
    /// 查找词条并跟随 @@@LINK 跳转，返回（词头, 词条内容）。大小写完全一致的词条排在前面。
    /// </summary>
    public List<(string Key, string Content)> Lookup(string word)
    {
        var results = new List<(string, string)>();
        var visited = new HashSet<int>();
        foreach (var index in Find(word))
            Follow(index, 0);
        return results;

        void Follow(int index, int depth)
        {
            if (depth > MaxLinkDepth || !visited.Add(index))
                return;
            var text = _mdx.ReadText(index);
            if (text.StartsWith(LinkPrefix, StringComparison.Ordinal))
            {
                var target = text[LinkPrefix.Length..].Split('\n')[0].Trim();
                foreach (var next in Find(target))
                    Follow(next, depth + 1);
                return;
            }
            results.Add((_mdx.GetKey(index), text));
        }
    }

    public bool HasResource(string path) => FindResource(path) != null;

    public byte[]? ReadResource(string path)
    {
        var found = FindResource(path);
        return found == null ? null : _resources[found.Value.File].ReadRecord(found.Value.Index);
    }

    public void Dispose()
    {
        _mdx.Dispose();
        _resources.ForEach(r => r.Dispose());
    }

    private (int File, int Index)? FindResource(string path)
    {
        var key = NormalizeResourcePath(path);
        for (var f = 0; f < _resourceIndex.Count; f++)
            if (_resourceIndex[f].TryGetValue(key, out var i))
                return (f, i);
        return null;
    }

    private List<int> Find(string word)
    {
        word = word.Trim();
        var hits = _index.Find(word);
        if (hits.Count > 0)
        {
            // 大小写一致的排前面：查 apple 时先显示 apple，再显示 Apple
            return [.. hits.OrderBy(i => _mdx.GetKey(i).Trim() == word ? 0 : 1)];
        }
        return _strippedIndex?.Find(StripKey(word)) ?? [];
    }

    /// <summary>MDict 的 StripKey：忽略大小写，只比较字母和数字；另外忽略变音符号（coupé = coupe）。</summary>
    private static string StripKey(string key)
    {
        // 大多数词条本来就是小写字母，直接复用原字符串，省内存
        if (key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            return key;
        var sb = new StringBuilder(key.Length);
        foreach (var c in key.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string NormalizeResourcePath(string path) =>
        path.StartsWith('\\') && !path.Contains('/') ? path : "\\" + path.Replace('/', '\\').TrimStart('\\');

    /// <summary>键 → 词条序号。绝大多数键只对应一条，重复的另存，避免给每个键都分配列表。</summary>
    private sealed class KeyIndex(int capacity, StringComparer comparer)
    {
        private readonly Dictionary<string, int> _first = new(capacity, comparer);
        private readonly Dictionary<string, List<int>> _more = new(comparer);

        public void Add(string key, int index)
        {
            if (_first.TryAdd(key, index))
                return;
            if (_more.TryGetValue(key, out var list))
                list.Add(index);
            else
                _more[key] = [index];
        }

        public List<int> Find(string key)
        {
            if (!_first.TryGetValue(key, out var first))
                return [];
            return _more.TryGetValue(key, out var more) ? [first, .. more] : [first];
        }
    }

    private static string StableId(string path)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return Convert.ToHexStringLower(bytes)[..10];
    }
}
