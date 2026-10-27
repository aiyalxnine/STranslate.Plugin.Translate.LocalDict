using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace STranslate.Plugin.Translate.LocalDict.Core;

/// <summary>
/// 解析朗文（LDOCE5++/6++ 等）MDX 的词条 HTML：
/// span.entryhead（hwd / proncodes / pos / inflections / sound://）
/// span.sense（sensenum / signpost / def&gt;EN+TRAN / example&gt;EXAEN+EXAMPLE / subsense）
/// span.phrvbentry &gt; span.entryhead &gt; span.phrvbhwd（短语动词）
/// <para>
/// 这类词库的 span 经常不闭合，HTML 解析后的树和浏览器里看到的层级并不一致：
/// 义项可能嵌在词头里，也可能被挤到文档顶层。因此不依赖嵌套关系，
/// 而是按文档顺序线性扫描：遇到词头就开始新词条，之后的义项都归它。
/// </para>
/// </summary>
public static partial class LongmanParser
{
    private static readonly string[] NoiseClasses = ["at-link", "popup-button", "collobox", "thesbox", "usagebox"];
    private static readonly string[] NotHeadClasses = ["sense", "subsense", "phrvbentry", "entryhead", "entry"];

    public static bool CanParse(string html) =>
        html.Contains("class=\"entryhead\"", StringComparison.Ordinal) &&
        html.Contains("class=\"sense", StringComparison.Ordinal);

    public static List<WordEntry> Parse(string html) =>
        Scan(Load(html)).Entries
            .Where(e => e.Senses.Count > 0 || e.Pronunciations.Count > 0 || e.PhrasalVerbs.Count > 0 || e.Forms.Count > 0)
            .ToList();

    /// <summary>从基础词的词条里取出与 <paramref name="phrase"/> 对应的短语动词。</summary>
    public static List<WordEntry> ParsePhrasalVerb(string html, string phrase)
    {
        var wanted = NormalizePhrase(phrase);
        return Scan(Load(html)).PhrasalVerbs
            .Where(p => p.Key == wanted && p.Entry.Senses.Count > 0)
            .Select(p => p.Entry)
            .ToList();
    }

    #region 扫描

    private static HtmlNode Load(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = doc.DocumentNode;
        var noise = root.Descendants()
            .Where(n => n.Name is "script" or "style" or "link" || NoiseClasses.Any(c => HasClass(n, c)))
            .ToList();
        foreach (var node in noise)
            node.Remove();
        return root;
    }

    private static (List<WordEntry> Entries, List<(string Key, WordEntry Entry)> PhrasalVerbs) Scan(HtmlNode root)
    {
        var entries = new List<WordEntry>();
        var phrasalVerbs = new List<(string, WordEntry)>();
        WordEntry? main = null, phrasal = null;

        foreach (var node in root.Descendants())
        {
            if (node.NodeType != HtmlNodeType.Element)
                continue;

            if (HasClass(node, "entryhead"))
            {
                var isMain = node.Descendants().Any(n => HasClass(n, "hwd") && InHead(n, node));
                var pvHead = isMain ? null : node.Descendants().FirstOrDefault(n => HasClass(n, "phrvbhwd"));
                if (pvHead != null)
                {
                    phrasal = new WordEntry
                    {
                        Headword = Text(pvHead),
                        PartOfSpeech = Text(node.Descendants().FirstOrDefault(n => HasClass(n, "pos")))
                    };
                    phrasalVerbs.Add((NormalizePhrase(PhraseWithoutObjects(pvHead)), phrasal));
                    if (main != null && !main.PhrasalVerbs.Contains(phrasal.Headword))
                        main.PhrasalVerbs.Add(phrasal.Headword);
                }
                else
                {
                    main = new WordEntry();
                    phrasal = null;
                    ParseHead(node, main);
                    entries.Add(main);
                }
                continue;
            }

            if (HasClass(node, "sense") && !HasAncestorWithClass(node, "sense", root))
            {
                var target = phrasal ?? main;
                if (target == null)
                {
                    target = main = new WordEntry();
                    entries.Add(main);
                }
                AddSenses(node, target);
            }
        }

        return (entries, phrasalVerbs);
    }

    private static void ParseHead(HtmlNode head, WordEntry entry)
    {
        // 只看词头本身：排除嵌进来的义项/短语动词/下一个词条；词形变化（如 ran /ræn/）单独处理，不能当成主音标
        var headNodes = head.Descendants().Where(n => InHead(n, head)).ToList();
        var inflections = headNodes.Where(n => HasClass(n, "inflections")).ToList();
        bool InInflections(HtmlNode n) => inflections.Any(i => IsAncestor(i, n));

        entry.Headword = Text(headNodes.FirstOrDefault(n => HasClass(n, "hwd")));
        entry.PartOfSpeech = string.Join(", ", headNodes
            .Where(n => HasClass(n, "pos") && !InInflections(n))
            .Select(n => Text(n).Trim(',', ' '))
            .Where(s => s.Length > 0)
            .Distinct());

        var proncodes = headNodes.FirstOrDefault(n => HasClass(n, "proncodes") && !InInflections(n));
        string bre = string.Empty, ame = string.Empty;
        if (proncodes != null)
        {
            bre = Text(FirstWithClass(proncodes, "pron"));
            var ameNode = FirstWithClass(proncodes, "amevarpron");
            ame = ameNode == null ? string.Empty : Text(ameNode).TrimStart('$').Trim();
        }

        string? breAudio = null, ameAudio = null;
        foreach (var link in headNodes.Where(a => a.Name == "a" && !InInflections(a)))
        {
            var path = SoundPath(link);
            if (path == null) continue;
            if (breAudio == null && path.Contains("/bre/", StringComparison.OrdinalIgnoreCase)) breAudio = path;
            else if (ameAudio == null && path.Contains("/ame/", StringComparison.OrdinalIgnoreCase)) ameAudio = path;
            else breAudio ??= path;
        }

        if (bre.Length > 0)
        {
            entry.Pronunciations.Add(new Pronunciation("英", bre, breAudio));
            entry.Pronunciations.Add(new Pronunciation("美", ame.Length > 0 ? ExpandPartialPron(bre, ame) : bre, ameAudio));
        }

        foreach (var tag in headNodes
                     .Where(n => (HasClass(n, "freq") || HasClass(n, "level") || HasClass(n, "registerlab") || HasClass(n, "geo") || HasClass(n, "gram"))
                                 && !InInflections(n) && !HasAncestorWithClass(n, "variant", head))
                     .Select(n => HasClass(n, "gram") ? Labels.Grammar(Text(n))
                         : HasClass(n, "registerlab") || HasClass(n, "geo") ? Labels.RegisterText(Text(n))
                         : Text(n)))
        {
            if (tag.Length > 0 && !entry.Tags.Contains(tag))
                entry.Tags.Add(tag);
        }

        foreach (var infl in inflections)
            ParseInflections(infl, entry);
    }

    private const string IpaVowels = "aeiouyæɑɒɔəɛɜɪʊʌø";

    /// <summary>
    /// 朗文的美音常只写与英音不同的部分：
    /// 结尾替换 hello /həˈləʊ/ $ -ˈloʊ → həˈloʊ；开头替换 betrothal /bɪˈtrəʊðəl/ $ bɪˈtroʊ- → bɪˈtroʊðəl。
    /// 中段替换 antibiotic /ˌæntɪbaɪˈɒtɪk/ $ -ˈɑː- → ˌæntɪbaɪˈɑːtɪk。
    /// 按“元音组 / 单个辅音”逐个对齐：元音组一对一替换，辅音必须相同（美音多出的 r 视为插入）。
    /// 对不齐或位置不唯一（如 SOS 的 -oʊ-）时原样保留，宁可不补也不拼错。
    /// </summary>
    internal static string ExpandPartialPron(string full, string partial)
    {
        var comma = partial.IndexOf(',');
        var first = (comma < 0 ? partial : partial[..comma]).Trim();
        var rest = comma < 0 ? string.Empty : partial[comma..];
        var main = full.Split(',')[0].Trim();
        var head = first.StartsWith('-');
        var tail = first.EndsWith('-');
        var core = first.Trim('-');
        if ((!head && !tail) || main.Length == 0 || core.Length == 0)
            return partial;

        var piece = PronTokens(core);
        var tokens = PronTokens(main);

        if (head && tail)
        {
            // 中段：找唯一一个能对齐的起点
            if (piece.All(IsVowelToken))
                return partial;
            var matches = Enumerable.Range(0, tokens.Count)
                .Where(s => IsVowelToken(piece[0]) ? IsVowelToken(tokens[s]) : tokens[s] == piece[0])
                .Select(s => (Start: s, Length: AlignPron(piece, tokens.GetRange(s, tokens.Count - s))))
                .Where(m => m.Length > 0)
                .ToList();
            if (matches.Count != 1)
                return partial;
            var (start, length) = matches[0];
            return string.Concat(tokens.Take(start)) + core + string.Concat(tokens.Skip(start + length)) + rest;
        }

        if (head)
        {
            piece.Reverse();
            tokens.Reverse();
        }
        var consumed = AlignPron(piece, tokens);
        if (consumed < 0)
            return partial;

        var kept = tokens.Skip(consumed);
        return (head ? string.Concat(kept.Reverse()) + core : core + string.Concat(kept)) + rest;
    }

    private static bool IsVowelToken(string token) => IpaVowels.Contains(token[0]);

    /// <summary>逐个对齐，返回在 <paramref name="tokens"/> 中消耗的个数；失败返回 -1。</summary>
    private static int AlignPron(List<string> piece, List<string> tokens)
    {
        var j = 0;
        foreach (var t in piece)
        {
            var isVowel = IsVowelToken(t);
            if (j < tokens.Count && isVowel && IsVowelToken(tokens[j]))
                j++;
            else if (j < tokens.Count && !isVowel && tokens[j] == t)
                j++;
            else if (t != "r")
                return -1;
        }
        return j;
    }

    /// <summary>拆成元音组（含长音符 ː）和单个其他字符。</summary>
    private static List<string> PronTokens(string pron)
    {
        var tokens = new List<string>();
        for (var i = 0; i < pron.Length;)
        {
            if (IpaVowels.Contains(pron[i]))
            {
                var start = i;
                while (i < pron.Length && (IpaVowels.Contains(pron[i]) || pron[i] == 'ː')) i++;
                tokens.Add(pron[start..i]);
            }
            else
            {
                tokens.Add(pron[i].ToString());
                i++;
            }
        }
        return tokens;
    }

    private static void ParseInflections(HtmlNode inflections, WordEntry entry)
    {
        foreach (var node in inflections.ChildNodes)
        {
            WordForm[] forms = ClassOf(node) switch
            {
                "pluralform" or "plural" => [WordForm.Plural],
                "pasttense" => [WordForm.PastTense],
                "pastpart" => [WordForm.PastParticiple],
                "ptandpp" => [WordForm.PastTense, WordForm.PastParticiple],
                "prespart" => [WordForm.PresentParticiple],
                "t3perssing" => [WordForm.ThirdPersonSingular],
                "comp" => [WordForm.Comparative],
                "superl" => [WordForm.Superlative],
                _ => []
            };
            if (forms.Length == 0) continue;

            // 去掉 "past tense"、"plural" 之类的说明文字，只留词形本身
            var value = string.Concat(node.ChildNodes
                .Where(c => c.NodeType == HtmlNodeType.Text || !(HasClass(c, "infllab") || HasClass(c, "italic") || HasClass(c, "neutral") || HasClass(c, "proncodes")))
                .Select(c => c.InnerText));
            foreach (var word in FormSeparatorRegex().Split(Clean(value)))
            {
                var w = word.Trim(' ', ',', '(', ')');
                if (w.Length == 0 || w.Length > 40) continue;
                foreach (var form in forms)
                    entry.AddForm(form, w);
            }
        }
    }

    #endregion

    #region 义项

    private static void AddSenses(HtmlNode senseNode, WordEntry entry)
    {
        var subsenses = senseNode.Descendants().Where(n => HasClass(n, "subsense")).ToList();
        bool InSub(HtmlNode n) => subsenses.Any(s => s == n || IsAncestor(s, n));

        var number = Text(senseNode.Descendants().FirstOrDefault(n => HasClass(n, "sensenum") && !InSub(n)));
        var labels = SenseLabels(senseNode, n => !InSub(n));

        if (subsenses.Count == 0)
        {
            AddSense(entry, senseNode, number, labels, _ => true);
            return;
        }

        // 主义项自己有释义时单独成行；只有例句时，例句并入第一个子义项
        var parent = AddSense(entry, senseNode, number, labels, n => !InSub(n), requireDefinition: true);
        Sense? firstSub = null;
        for (var i = 0; i < subsenses.Count; i++)
        {
            var sub = subsenses[i];
            var subNumber = Text(FirstWithClass(sub, "sensenum")).TrimEnd(')', '.');
            if (subNumber.Length == 0 && subsenses.Count > 1)
                subNumber = ((char)('a' + i)).ToString();
            var subLabels = SenseLabels(sub, _ => true);
            if (i == 0 && parent == null)
                subLabels.InsertRange(0, labels.Where(l => !subLabels.Contains(l)));
            var added = AddSense(entry, sub, number + subNumber, subLabels, _ => true);
            firstSub ??= added;
        }

        if (parent == null && firstSub != null)
        {
            foreach (var exampleNode in senseNode.Descendants().Where(n => HasClass(n, "example") && !InSub(n)))
                if (ParseExample(exampleNode) is { } example)
                    firstSub.Examples.Add(example);
        }
    }

    private static List<string> SenseLabels(HtmlNode scope, Func<HtmlNode, bool> include)
    {
        // 义项标签（路标、语法、语体、地域）都写在释义之前；释义、例句、子义项之后出现的标签只修饰某一句
        var cutoff = scope.Descendants()
            .FirstOrDefault(n => HasClass(n, "def") || HasClass(n, "example") || HasClass(n, "subsense") || HasClass(n, "gramexa") || HasClass(n, "colloexa"))
            ?.StreamPosition ?? int.MaxValue;

        var labels = new List<string>();
        foreach (var node in scope.Descendants().Where(include))
        {
            if (node.StreamPosition >= cutoff)
                break;

            // 同义说法（span.variant，如 not a happy camper American English）里的标签不属于本义项
            if (HasAncestorWithClass(node, "variant", scope))
                continue;

            string? label = ClassOf(node) switch
            {
                "signpost" => Signpost(node),
                "lexunit" => Text(node),
                "gram" => Labels.Grammar(Text(node)),
                "registerlab" or "geo" => Bracket(Labels.RegisterText(Text(node))),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(label) && !labels.Contains(label))
                labels.Add(label);
        }
        return labels;
    }

    private static string? Bracket(string text) => text.Length == 0 ? null : $"〈{text}〉";

    private static string? Signpost(HtmlNode node)
    {
        var cn = Text(node.Element("sign"));
        if (cn.Length > 0) return $"〔{cn}〕";
        var en = Text(node.Element("signen"));
        if (en.Length == 0) en = Text(node);
        return en.Length > 0 ? $"〔{en}〕" : null;
    }

    private static Sense? AddSense(WordEntry entry, HtmlNode scope, string number, List<string> labels,
        Func<HtmlNode, bool> include, bool requireDefinition = false)
    {
        // 有的词库把例句也包在 span.def 里，这种“释义”不算数
        var def = scope.Descendants().FirstOrDefault(n =>
            HasClass(n, "def") && include(n) && !HasAncestorWithClass(n, "example", scope) &&
            (n.Element("en") != null || n.Element("tran") != null || !n.Descendants().Any(d => HasClass(d, "example"))));
        string? definition = null, translation = null;
        if (def != null)
        {
            var en = def.Element("en");
            var tran = def.Element("tran");
            definition = en != null ? Text(en) : tran != null ? null : Text(def);
            translation = tran != null ? Text(tran) : null;

            // went → "the past tense of go"：记下原形，方便补充原形词条
            if (definition != null && InflectionOfRegex().IsMatch(definition))
            {
                var baseWord = Text(def.Descendants().FirstOrDefault(n => HasClass(n, "refhwd")));
                if (baseWord.Length > 0 && !entry.BaseWords.Contains(baseWord, StringComparer.OrdinalIgnoreCase))
                    entry.BaseWords.Add(baseWord);
            }
        }

        if (requireDefinition && def == null)
            return null;

        var sense = new Sense
        {
            Number = number,
            Definition = string.IsNullOrWhiteSpace(definition) ? null : definition,
            Translation = string.IsNullOrWhiteSpace(translation) ? null : translation
        };
        sense.Labels.AddRange(labels);

        foreach (var exampleNode in scope.Descendants().Where(n => HasClass(n, "example") && include(n)))
        {
            var example = ParseExample(exampleNode);
            if (example != null)
                sense.Examples.Add(example);
        }

        if (sense.Definition == null && sense.Translation == null && sense.Examples.Count == 0)
            return null;
        entry.Senses.Add(sense);
        return sense;
    }

    private static Example? ParseExample(HtmlNode node)
    {
        var en = node.Element("exaen");
        var cn = node.Element("example");
        var text = en != null ? Text(en) : Text(node);
        if (text.Length == 0)
            return null;
        var audio = node.Descendants("a").Select(SoundPath).FirstOrDefault(p => p != null);
        var translation = cn == null ? null : Text(cn);
        return new Example(text, string.IsNullOrWhiteSpace(translation) ? null : translation, audio);
    }

    #endregion

    #region 工具

    internal static string Text(HtmlNode? node)
    {
        if (node == null) return string.Empty;
        return Clean(node.InnerText);
    }

    internal static string Clean(string raw) =>
        Labels.Punctuation(WhitespaceRegex().Replace(HtmlEntity.DeEntitize(raw), " ").Trim());

    /// <summary>sound://hwd/bre/7/x.mp3 → hwd/bre/7/x.mp3</summary>
    internal static string? SoundPath(HtmlNode link)
    {
        var href = link.GetAttributeValue("href", string.Empty);
        if (!href.StartsWith("sound://", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Uri.UnescapeDataString(href["sound://".Length..]).Replace('\\', '/').TrimStart('/');
        return path.Length == 0 ? null : path;
    }

    private static string PhraseWithoutObjects(HtmlNode hwd) =>
        Clean(string.Concat(hwd.ChildNodes.Where(c => !HasClass(c, "object")).Select(c => c.InnerText)));

    internal static string NormalizePhrase(string phrase) =>
        WhitespaceRegex().Replace(phrase.ToLowerInvariant().Replace('’', '\''), " ").Trim();

    private static string ClassOf(HtmlNode node) =>
        node.GetAttributeValue("class", string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    internal static bool HasClass(HtmlNode? node, string cls)
    {
        if (node == null || node.NodeType != HtmlNodeType.Element) return false;
        var value = node.GetAttributeValue("class", string.Empty);
        if (value.Length == 0) return false;
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part.Equals(cls, StringComparison.Ordinal)) return true;
        return false;
    }

    private static HtmlNode? FirstWithClass(HtmlNode? scope, string cls) =>
        scope?.Descendants().FirstOrDefault(n => HasClass(n, cls));

    private static bool HasAncestorWithClass(HtmlNode node, string cls, HtmlNode stopAt)
    {
        for (var p = node.ParentNode; p != null && p != stopAt; p = p.ParentNode)
            if (HasClass(p, cls)) return true;
        return false;
    }

    /// <summary>节点属于该词头本身，而不是嵌进来的义项、短语动词或下一个词条。</summary>
    private static bool InHead(HtmlNode node, HtmlNode head)
    {
        for (var p = node; p != null && p != head; p = p.ParentNode)
            if (NotHeadClasses.Any(c => HasClass(p, c))) return false;
        return true;
    }

    private static bool IsAncestor(HtmlNode ancestor, HtmlNode node)
    {
        for (var p = node.ParentNode; p != null; p = p.ParentNode)
            if (p == ancestor) return true;
        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@",|\s+or\s+|\s+also\s+")]
    private static partial Regex FormSeparatorRegex();

    [GeneratedRegex(@"^(an? |the )?(.+ )?(past tense|past participle|present participle|plural|comparative|superlative|third person singular)( form)? of ", RegexOptions.IgnoreCase)]
    private static partial Regex InflectionOfRegex();

    #endregion
}
