using System.Text.RegularExpressions;

namespace STranslate.Plugin.Translate.LocalDict.Core;

/// <summary>义项标签的显示形式：语法码缩写、语体/地域标签中文化、标点规范化。</summary>
public static partial class Labels
{
    private static readonly Dictionary<string, string> Register = new(StringComparer.OrdinalIgnoreCase)
    {
        ["British English"] = "英式",
        ["American English"] = "美式",
        ["Australian English"] = "澳式",
        ["Scottish English"] = "苏格兰英语",
        ["Irish English"] = "爱尔兰英语",
        ["informal"] = "非正式",
        ["formal"] = "正式",
        ["spoken"] = "口语",
        ["written"] = "书面语",
        ["old-fashioned"] = "过时",
        ["old use"] = "旧时用法",
        ["literary"] = "文学用语",
        ["poetic"] = "诗歌用语",
        ["technical"] = "术语",
        ["humorous"] = "幽默",
        ["not polite"] = "不礼貌",
        ["taboo"] = "禁忌语",
        ["slang"] = "俚语",
        ["nonstandard"] = "不规范",
        ["approving"] = "褒义",
        ["disapproving"] = "贬义",
        ["law"] = "法律",
        ["medical"] = "医学",
        ["biblical"] = "圣经用语",
        ["trademark"] = "商标"
    };

    /// <summary>[intransitive, transitive] → [I, T]；[countable usually singular] → [C usually singular]</summary>
    public static string Grammar(string text) =>
        GrammarRegex().Replace(text, m => m.Value.ToLowerInvariant() switch
        {
            "intransitive" => "I",
            "transitive" => "T",
            "uncountable" => "U",
            _ => "C"
        });

    /// <summary>informal → 非正式；especially British English → 尤英式；未知标签原样返回。</summary>
    public static string RegisterText(string text)
    {
        var t = text.Trim().TrimEnd(':', ',');
        var prefix = string.Empty;
        if (t.StartsWith("especially ", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "尤";
            t = t["especially ".Length..];
        }
        return Register.TryGetValue(t, out var cn) ? prefix + cn : text.Trim();
    }

    /// <summary>朗文数据里的小号标点（﹐﹔﹕）换成常规全角标点。</summary>
    public static string Punctuation(string text) =>
        text.Replace('﹐', '，').Replace('﹔', '；').Replace('﹕', '：').Replace('﹖', '？').Replace('﹗', '！');

    [GeneratedRegex(@"\b(intransitive|transitive|uncountable|countable)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GrammarRegex();
}
