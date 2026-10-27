namespace STranslate.Plugin.Translate.LocalDict;

public class Settings
{
    /// <summary>按顺序查询的 MDX 词库。</summary>
    public List<DictionarySetting> Dictionaries { get; set; } = [];

    /// <summary>中文释义后附上英文释义（英汉双解词库）。</summary>
    public bool ShowEnglishDefinition { get; set; } = true;

    /// <summary>在词条下列出短语动词。</summary>
    public bool ShowPhrasalVerbs { get; set; } = true;

    /// <summary>每次查询最多显示的例句数。</summary>
    public int MaxExamples { get; set; } = 6;

    /// <summary>本地发音服务的端口与令牌，固定下来以便历史记录里的发音地址在重启后可用。</summary>
    public int AudioPort { get; set; }

    public string AudioToken { get; set; } = string.Empty;
}

public class DictionarySetting
{
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
