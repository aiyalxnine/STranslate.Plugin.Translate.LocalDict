using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace STranslate.Plugin.Translate.LocalDict.Core;

/// <summary>
/// 非朗文结构的词库：把 HTML/纯文本词条转成逐行释义，并尽量找出音标和第一个发音。
/// </summary>
public static partial class GenericParser
{
    private const int MaxLines = 60;

    private static readonly HashSet<string> BlockTags =
    [
        "p", "div", "br", "li", "ul", "ol", "dl", "dt", "dd", "tr", "table", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "blockquote", "section"
    ];

    public static List<WordEntry> Parse(string content, string headword)
    {
        var entry = new WordEntry { Headword = headword };
        string text;
        string? audio = null;

        if (content.Contains('<') && content.Contains('>'))
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(content);
            var root = doc.DocumentNode;
            foreach (var node in root.Descendants().Where(n => n.Name is "script" or "style" or "link" or "head").ToList())
                node.Remove();
            audio = root.Descendants("a").Select(LongmanParser.SoundPath).FirstOrDefault(p => p != null);

            var sb = new StringBuilder();
            AppendText(root, sb);
            text = sb.ToString();
        }
        else
        {
            text = content;
        }

        var lines = text.Split('\n')
            .Select(l => LongmanParser.Clean(l))
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count > 0 && lines[0].Equals(headword, StringComparison.OrdinalIgnoreCase))
            lines.RemoveAt(0);

        var phonetic = PhoneticRegex().Match(text);
        if (phonetic.Success)
        {
            var value = phonetic.Value.Trim('/', '[', ']', ' ');
            entry.Pronunciations.Add(new Pronunciation("音", value, audio));
        }

        string? previous = null;
        foreach (var line in lines.Take(MaxLines))
        {
            if (line == previous) continue;
            previous = line;
            entry.Senses.Add(new Sense { Translation = line });
        }

        return entry.Senses.Count > 0 ? [entry] : [];
    }

    private static void AppendText(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Text:
                    sb.Append(child.InnerText);
                    break;
                case HtmlNodeType.Element:
                    var block = BlockTags.Contains(child.Name);
                    if (block) sb.Append('\n');
                    AppendText(child, sb);
                    if (block) sb.Append('\n');
                    break;
            }
        }
    }

    // 仅接受含 IPA 特征字符的 /…/ 或 […]，避免把普通斜杠内容当成音标
    [GeneratedRegex(@"(/|\[)[^/\[\]\n]{0,40}[ˈˌəɪʊæɑɔʃʒθðŋːɜʌɒ][^/\[\]\n]{0,40}(/|\])")]
    private static partial Regex PhoneticRegex();
}
