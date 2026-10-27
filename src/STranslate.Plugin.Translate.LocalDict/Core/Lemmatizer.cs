namespace STranslate.Plugin.Translate.LocalDict.Core;

/// <summary>
/// 查不到原词时，按英语常见屈折规则猜测原形（running → run，studies → study，bigger → big）。
/// 只生成候选，是否存在由词库决定。
/// </summary>
public static class Lemmatizer
{
    public static List<string> Candidates(string word)
    {
        var result = new List<string>();
        var w = word.ToLowerInvariant();
        if (w.Length < 4 || !w.All(char.IsAsciiLetterLower))
            return result;

        void Add(string s)
        {
            if (s.Length >= 2 && s != w && !result.Contains(s))
                result.Add(s);
        }

        void AddUndoubled(string stem)
        {
            // stopped → stop，bigger → big
            if (stem.Length >= 3 && stem[^1] == stem[^2] && !"aeiou".Contains(stem[^1]))
                Add(stem[..^1]);
        }

        if (w.EndsWith("ies")) Add(w[..^3] + "y");
        if (w.EndsWith("ves")) { Add(w[..^3] + "f"); Add(w[..^3] + "fe"); }
        if (w.EndsWith("es")) Add(w[..^2]);
        if (w.EndsWith('s') && !w.EndsWith("ss")) Add(w[..^1]);

        if (w.EndsWith("ied")) Add(w[..^3] + "y");
        if (w.EndsWith("ed")) { AddUndoubled(w[..^2]); Add(w[..^2]); Add(w[..^1]); }

        if (w.EndsWith("ying")) Add(w[..^4] + "ie");
        if (w.EndsWith("ing")) { AddUndoubled(w[..^3]); Add(w[..^3]); Add(w[..^3] + "e"); }

        if (w.EndsWith("ier")) Add(w[..^3] + "y");
        if (w.EndsWith("iest")) Add(w[..^4] + "y");
        if (w.EndsWith("er")) { AddUndoubled(w[..^2]); Add(w[..^2]); Add(w[..^1]); }
        if (w.EndsWith("est")) { AddUndoubled(w[..^3]); Add(w[..^3]); Add(w[..^2]); }

        if (w.EndsWith("ily")) Add(w[..^3] + "y");
        if (w.EndsWith("ly")) Add(w[..^2]);

        return result;
    }
}
