namespace STranslate.Plugin.Translate.LocalDict;

/// <summary>插件文案：优先取 Languages 里的翻译，取不到时用内置中文。</summary>
internal static class Texts
{
    private const string Prefix = "STranslate_Plugin_Translate_LocalDict_";

    private static readonly Dictionary<string, string> Fallback = new()
    {
        ["NoDictionary"] = "尚未添加词库：请在本插件的设置中添加 MDX 文件",
        ["PhrasalVerbs"] = "短语动词",
        ["Loading"] = "正在载入…",
        ["EntryCount"] = "{0:N0} 个词条",
        ["WithAudio"] = "发音资源 {0:N0} 个",
        ["NoAudio"] = "未找到同名 .mdd，无法播放发音",
        ["Duplicate"] = "已在列表中",
        ["SelectDictionary"] = "选择 MDX 词库"
    };

    public static string Get(IPluginContext? context, string key)
    {
        try
        {
            var value = context?.GetTranslation(Prefix + key);
            if (!string.IsNullOrEmpty(value) && value != Prefix + key)
                return value;
        }
        catch
        {
            // 取翻译失败时用内置文案
        }
        return Fallback.GetValueOrDefault(key, key);
    }
}
