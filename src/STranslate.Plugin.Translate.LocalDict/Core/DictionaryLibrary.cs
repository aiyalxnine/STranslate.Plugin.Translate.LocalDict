using System.Text.RegularExpressions;

namespace STranslate.Plugin.Translate.LocalDict.Core;

/// <summary>
/// 管理已打开的词库（按路径缓存、后台载入）并执行跨词库查询。
/// </summary>
public sealed partial class DictionaryLibrary : IDisposable
{
    private const int MaxQueryLength = 60;
    private const int MaxQueryWords = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, Task<LoadedDictionary>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>取得（必要时在后台载入）词库。载入失败的任务不会被缓存。</summary>
    public Task<LoadedDictionary> GetAsync(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(path, out var task) && !task.IsFaulted && !task.IsCanceled)
                return task;

            if (!File.Exists(path))
                return Task.FromException<LoadedDictionary>(new FileNotFoundException("词库文件不存在", path));

            task = Task.Run(() => LoadedDictionary.Open(path));
            _cache[path] = task;
            return task;
        }
    }

    public LoadedDictionary? FindById(string id)
    {
        lock (_gate)
        {
            return _cache.Values
                .Where(t => t.IsCompletedSuccessfully)
                .Select(t => t.Result)
                .FirstOrDefault(d => d.Id == id);
        }
    }

    /// <summary>关闭不在列表中的词库（设置里被移除时）。</summary>
    public void Retain(IEnumerable<string> paths)
    {
        var keep = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        List<Task<LoadedDictionary>> removed;
        lock (_gate)
        {
            var keys = _cache.Keys.Where(k => !keep.Contains(k)).ToList();
            removed = keys.Select(k => _cache[k]).ToList();
            keys.ForEach(k => _cache.Remove(k));
        }
        removed.ForEach(DisposeWhenLoaded);
    }

    /// <summary>重新打开某本词库（文件被替换后使用）。</summary>
    public void Reload(string path)
    {
        Task<LoadedDictionary>? old;
        lock (_gate)
        {
            _cache.Remove(path, out old);
        }
        if (old != null)
            DisposeWhenLoaded(old);
    }

    public async Task<List<DictionaryHit>> LookupAsync(string text, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var query = NormalizeQuery(text);
        if (query == null || paths.Count == 0)
            return [];

        var hits = new List<DictionaryHit>();
        var errors = new List<string>();
        foreach (var path in paths)
        {
            LoadedDictionary dictionary;
            try
            {
                dictionary = await GetAsync(path).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
                continue;
            }

            var entries = await Task.Run(() => LookupIn(dictionary, query, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (entries.Count > 0)
                hits.Add(new DictionaryHit(dictionary.Id, dictionary.Title, entries));
        }

        // 所有词库都打不开时，把原因报给用户；部分失败时照常返回其余结果
        if (hits.Count == 0 && errors.Count == paths.Count)
            throw new InvalidOperationException(string.Join("；", errors));
        return hits;
    }

    public static List<WordEntry> LookupIn(LoadedDictionary dictionary, string query, CancellationToken cancellationToken = default)
    {
        foreach (var form in QueryVariants(query))
        {
            var records = dictionary.Lookup(form);
            if (records.Count > 0)
                return WithBaseEntries(dictionary, ParseRecords(records, cancellationToken), cancellationToken);
        }

        var space = query.IndexOf(' ');
        if (space < 0)
        {
            foreach (var lemma in Lemmatizer.Candidates(query))
            {
                var records = dictionary.Lookup(lemma);
                if (records.Count > 0)
                    return ParseRecords(records, cancellationToken);
            }
            return [];
        }

        // 短语动词（如 look up / ran across）通常不单独成条，到动词原形的词条里找
        var verb = query[..space];
        var rest = query[(space + 1)..];
        var verbForms = new[] { verb }
            .Concat(Lemmatizer.Candidates(verb))
            .Concat(ParseRecords(dictionary.Lookup(verb), cancellationToken).SelectMany(e => e.BaseWords))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in verbForms)
        {
            foreach (var (_, content) in dictionary.Lookup(candidate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!LongmanParser.CanParse(content)) continue;
                var entries = LongmanParser.ParsePhrasalVerb(content, $"{candidate} {rest}");
                if (entries.Count > 0)
                    return entries;
            }
        }
        return [];
    }

    public static string? NormalizeQuery(string text)
    {
        var q = WhitespaceRegex().Replace(text, " ").Trim().Trim(TrimChars);
        if (q.Length == 0 || q.Length > MaxQueryLength || q.Count(c => c == ' ') >= MaxQueryWords)
            return null;
        return q;
    }

    public void Dispose()
    {
        List<Task<LoadedDictionary>> all;
        lock (_gate)
        {
            _disposed = true;
            all = [.. _cache.Values];
            _cache.Clear();
        }
        all.ForEach(DisposeWhenLoaded);
    }

    private static List<WordEntry> ParseRecords(List<(string Key, string Content)> records, CancellationToken cancellationToken)
    {
        var entries = new List<WordEntry>();
        foreach (var (key, content) in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = LongmanParser.CanParse(content) ? LongmanParser.Parse(content) : [];
            if (parsed.Count == 0)
                parsed = GenericParser.Parse(content, key);
            entries.AddRange(parsed);
        }
        return entries;
    }

    /// <summary>went、children 这类只有“某词的过去式/复数”一句话的词条，后面补上原形的完整词条。</summary>
    private static List<WordEntry> WithBaseEntries(LoadedDictionary dictionary, List<WordEntry> entries, CancellationToken cancellationToken)
    {
        var baseWords = entries
            .Where(e => e.Senses.Count <= 2)
            .SelectMany(e => e.BaseWords)
            .Where(w => !entries.Any(e => e.Headword.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var word in baseWords)
            entries.AddRange(ParseRecords(dictionary.Lookup(word), cancellationToken));
        return entries;
    }

    private static IEnumerable<string> QueryVariants(string query)
    {
        yield return query;
        if (query.Contains('\''))
            yield return query.Replace('\'', '’');
        else if (query.Contains('’'))
            yield return query.Replace('’', '\'');
    }

    private static void DisposeWhenLoaded(Task<LoadedDictionary> task) =>
        task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
        }, TaskScheduler.Default);

    private static readonly char[] TrimChars =
        ['"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', '{', '}', '<', '>', '.', ',', ';', ':', '!', '?', '，', '。', '；', '：', '！', '？', '、', '《', '》', '「', '」', '…', '*', '-', '—'];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
