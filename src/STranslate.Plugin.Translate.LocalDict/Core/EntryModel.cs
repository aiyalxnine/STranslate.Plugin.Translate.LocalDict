namespace STranslate.Plugin.Translate.LocalDict.Core;

/// <summary>词形变化种类，对应 STranslate 词典结果里的各栏。</summary>
public enum WordForm
{
    Plural,
    PastTense,
    PastParticiple,
    PresentParticiple,
    ThirdPersonSingular,
    Comparative,
    Superlative
}

/// <summary>音标与发音。AudioPath 是 MDD 内的资源路径（如 hwd/bre/7/nausea0205.mp3）。</summary>
public sealed record Pronunciation(string Label, string Phonetic, string? AudioPath);

public sealed record Example(string Text, string? Translation, string? AudioPath);

public sealed class Sense
{
    /// <summary>义项编号，如 "1"、"4a"；通用词库为空。</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>附加标签：路标、语法、语体、短语等，如 "〔奔跑〕"、"[intransitive]"。</summary>
    public List<string> Labels { get; } = [];

    public string? Translation { get; set; }
    public string? Definition { get; set; }
    public List<Example> Examples { get; } = [];
}

/// <summary>一个词条（同形异义词会拆成多个）。</summary>
public sealed class WordEntry
{
    public string Headword { get; set; } = string.Empty;
    public string PartOfSpeech { get; set; } = string.Empty;
    public List<Pronunciation> Pronunciations { get; } = [];
    public Dictionary<WordForm, List<string>> Forms { get; } = [];
    public List<string> Tags { get; } = [];
    public List<Sense> Senses { get; } = [];
    public List<string> PhrasalVerbs { get; } = [];

    /// <summary>变形词条（went = "the past tense of go"）指向的原形。</summary>
    public List<string> BaseWords { get; } = [];

    public void AddForm(WordForm form, string value)
    {
        if (!Forms.TryGetValue(form, out var list))
            Forms[form] = list = [];
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
            list.Add(value);
    }
}

/// <summary>某本词库的查询结果。</summary>
public sealed class DictionaryHit(string dictionaryId, string dictionaryTitle, List<WordEntry> entries)
{
    public string DictionaryId { get; } = dictionaryId;
    public string DictionaryTitle { get; } = dictionaryTitle;
    public List<WordEntry> Entries { get; } = entries;
}
