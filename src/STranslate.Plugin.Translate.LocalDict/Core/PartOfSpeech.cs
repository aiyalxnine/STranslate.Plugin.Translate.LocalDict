namespace STranslate.Plugin.Translate.LocalDict.Core;

public static class PartOfSpeech
{
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["noun"] = "n.",
        ["verb"] = "v.",
        ["adjective"] = "adj.",
        ["adverb"] = "adv.",
        ["preposition"] = "prep.",
        ["conjunction"] = "conj.",
        ["pronoun"] = "pron.",
        ["determiner"] = "det.",
        ["predeterminer"] = "predet.",
        ["quantifier"] = "quant.",
        ["interjection"] = "int.",
        ["number"] = "num.",
        ["prefix"] = "pref.",
        ["suffix"] = "suf.",
        ["abbreviation"] = "abbr.",
        ["phrasal verb"] = "phr v.",
        ["modal verb"] = "modal v.",
        ["auxiliary verb"] = "aux v.",
        ["linking verb"] = "link v."
    };

    /// <summary>义项行左侧的短标签：只取第一个词性（"interjection, noun" → int.），避免每行都很长。</summary>
    public static string Short(string partOfSpeech)
    {
        var first = partOfSpeech.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first == null ? string.Empty : Abbreviations.GetValueOrDefault(first, first);
    }

    /// <summary>noun → n.；"interjection, noun" → int./n.；未知词性原样返回。</summary>
    public static string Abbreviate(string partOfSpeech)
    {
        if (string.IsNullOrWhiteSpace(partOfSpeech))
            return string.Empty;
        return string.Join("/", partOfSpeech
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Abbreviations.GetValueOrDefault(p, p)));
    }
}
