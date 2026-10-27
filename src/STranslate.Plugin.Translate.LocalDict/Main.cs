using System.Collections.ObjectModel;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using STranslate.Plugin.Translate.LocalDict.Core;
using STranslate.Plugin.Translate.LocalDict.View;
using STranslate.Plugin.Translate.LocalDict.ViewModel;

namespace STranslate.Plugin.Translate.LocalDict;

public class Main : DictionaryPluginBase
{
    private const int MaxSenseRows = 80;
    private const int MaxSymbolGroups = 3;
    // 主窗口宽 470 时音标行约可容纳 445 个估算单位（按实际界面中 record 三组音标的占位校准），留一点余量
    private const double SymbolRowWidth = 430;
    private const int MaxTags = 8;

    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings _settings = null!;
    private IPluginContext _context = null!;
    private DictionaryLibrary _library = null!;
    private AudioServer _audio = null!;

    public override void Init(IPluginContext context)
    {
        _context = context;
        _settings = context.LoadSettingStorage<Settings>();
        if (string.IsNullOrEmpty(_settings.AudioToken))
        {
            _settings.AudioToken = Guid.NewGuid().ToString("N");
            context.SaveSettingStorage<Settings>();
        }

        _library = new DictionaryLibrary();
        _audio = new AudioServer(ResolveAudio, _settings.AudioPort, _settings.AudioToken, port =>
        {
            _settings.AudioPort = port;
            _context.SaveSettingStorage<Settings>();
        });

        // 后台预先建立索引，第一次查词不用等
        foreach (var path in EnabledPaths())
        {
            _library.GetAsync(path).ContinueWith(
                t => _context.Logger.LogWarning(t.Exception?.GetBaseException(), "本地词典载入失败: {Path}", path),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(_context, _settings, _library);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public override async Task TranslateAsync(string content, DictionaryResult result, CancellationToken cancellationToken = default)
    {
        var paths = EnabledPaths();
        if (paths.Count == 0)
            throw new InvalidOperationException(Texts.Get(_context, "NoDictionary"));

        var hits = await _library.LookupAsync(content, paths, cancellationToken).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            result.ResultType = DictionaryResultType.NoResult;
            return;
        }

        Fill(result, hits);
        result.ResultType = DictionaryResultType.Success;
    }

    public override void Dispose()
    {
        _audio?.Dispose();
        _library?.Dispose();
    }

    private List<string> EnabledPaths()
    {
        lock (_settings)
        {
            return _settings.Dictionaries
                .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.Path))
                .Select(d => d.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private byte[]? ResolveAudio(string dictionaryId, string path) =>
        _library.FindById(dictionaryId)?.ReadResource(path);

    #region 结果映射

    private void Fill(DictionaryResult result, List<DictionaryHit> hits)
    {
        var all = hits.SelectMany(h => h.Entries.Select(e => (Hit: h, Entry: e))).ToList();
        var headword = all.Select(x => x.Entry.Headword).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? string.Empty;
        result.Text = headword;

        var own = all.Where(x => x.Entry.Headword.Equals(headword, StringComparison.OrdinalIgnoreCase)).ToList();
        FillSymbols(result, own.Count > 0 ? own : all);
        FillMeans(result, hits, headword);
        FillForms(result, own.Select(x => x.Entry));
        FillSentences(result, all.Select(x => x.Entry));
    }

    private void FillSymbols(DictionaryResult result, List<(DictionaryHit Hit, WordEntry Entry)> entries)
    {
        // 同一个词不同词性读音不同（record n./v.）时，每组读音都列出并标上词性
        var groups = entries
            .Where(x => x.Entry.Pronunciations.Count > 0)
            .GroupBy(x => string.Join("|", x.Entry.Pronunciations.Select(p => p.Phonetic)))
            .Take(MaxSymbolGroups)
            .Select(g => g.First())
            .ToList();
        var withPos = groups.Count > 1;

        // 宿主的音标行是不换行的横排，放不下的会被挤出窗口。
        // 挑选优先级：各词性的英音（不同词性重音常不同，如 present adj./v.）→ 各词性的美音，
        // 按估算宽度放到满为止；显示时仍按词性分组、英在前美在后。
        var candidates = new List<(int Priority, int Display, string Label, Pronunciation Pron, string DictionaryId)>();
        for (var g = 0; g < groups.Count; g++)
        {
            var (hit, entry) = groups[g];
            var pos = withPos ? PartOfSpeech.Short(entry.PartOfSpeech) : string.Empty;
            for (var i = 0; i < entry.Pronunciations.Count; i++)
            {
                var p = entry.Pronunciations[i];
                var priority = i * groups.Count + g;
                candidates.Add((priority, g * 10 + i, pos.Length > 0 ? $"{p.Label} {pos}" : p.Label, p, hit.DictionaryId));
            }
        }

        var used = 0.0;
        var chosen = new List<(int Priority, int Display, string Label, Pronunciation Pron, string DictionaryId)>();
        foreach (var c in candidates.OrderBy(c => c.Priority))
        {
            var width = EstimateSymbolWidth(c.Label, c.Pron.Phonetic);
            if (chosen.Count > 0 && used + width > SymbolRowWidth)
                continue;
            used += width;
            chosen.Add(c);
        }

        foreach (var c in chosen.OrderBy(c => c.Display))
        {
            result.Symbols.Add(new Symbol
            {
                Label = c.Label,
                Phonetic = c.Pron.Phonetic,
                AudioUrl = AudioUrl(c.DictionaryId, c.Pron.AudioPath)
            });
        }
    }

    /// <summary>
    /// 按 STranslate 2.0.10 音标模板估算一项的宽度（与界面单位一致）：
    /// 标签徽章（10px 字号，左右各 4px 内边距）+ 5 + /音标/（Consolas 12px）+ 5 + 喇叭按钮 20 + 项间距 15。
    /// </summary>
    private static double EstimateSymbolWidth(string label, string phonetic)
    {
        var badge = 8 + label.Sum(c => c > 0x2E80 ? 10.0 : 5.5);
        return badge + 5 + 6.6 * (phonetic.Length + 2) + 5 + 20 + 15;
    }

    private void FillMeans(DictionaryResult result, List<DictionaryHit> hits, string headword)
    {
        var rows = 0;
        foreach (var hit in hits)
        {
            if (hits.Count > 1)
                result.DictMeans.Add(Row("▍", hit.DictionaryTitle));

            foreach (var entry in hit.Entries)
            {
                var pos = PartOfSpeech.Short(entry.PartOfSpeech);
                // went 之后补充的 go 词条：先用一行标出词头
                if (!entry.Headword.Equals(headword, StringComparison.OrdinalIgnoreCase) && entry.Headword.Length > 0)
                    result.DictMeans.Add(Row("→", string.IsNullOrEmpty(entry.PartOfSpeech) ? entry.Headword : $"{entry.Headword}  {entry.PartOfSpeech}"));

                foreach (var sense in entry.Senses)
                {
                    if (rows++ >= MaxSenseRows)
                        return;
                    var means = SenseText(sense);
                    if (means.Count == 0)
                        continue;
                    var label = sense.Number.Length > 0 ? pos + sense.Number : pos;
                    result.DictMeans.Add(new DictMean { PartOfSpeech = label, Means = new ObservableCollection<string>(means) });
                }

                if (_settings.ShowPhrasalVerbs && entry.PhrasalVerbs.Count > 0)
                    result.DictMeans.Add(new DictMean
                    {
                        PartOfSpeech = Texts.Get(_context, "PhrasalVerbs"),
                        Means = new ObservableCollection<string>(entry.PhrasalVerbs)
                    });
            }
        }
    }

    /// <summary>宿主用“；”连接各段：标签+中文释义 ； 英文释义</summary>
    private List<string> SenseText(Sense sense)
    {
        var labels = string.Join(" ", sense.Labels);
        var parts = new List<string>();
        if (sense.Translation != null)
        {
            parts.Add($"{labels} {sense.Translation}".Trim());
            if (_settings.ShowEnglishDefinition && sense.Definition != null)
                parts.Add(sense.Definition);
        }
        else if (sense.Definition != null)
        {
            parts.Add($"{labels} {sense.Definition}".Trim());
        }
        else if (labels.Length > 0)
        {
            parts.Add(labels);
        }
        return parts;
    }

    private static void FillForms(DictionaryResult result, IEnumerable<WordEntry> entries)
    {
        foreach (var entry in entries)
        {
            foreach (var (form, values) in entry.Forms)
            {
                var target = form switch
                {
                    WordForm.Plural => result.Plurals,
                    WordForm.PastTense => result.PastTense,
                    WordForm.PastParticiple => result.PastParticiple,
                    WordForm.PresentParticiple => result.PresentParticiple,
                    WordForm.ThirdPersonSingular => result.ThirdPersonSingular,
                    WordForm.Comparative => result.Comparative,
                    _ => result.Superlative
                };
                foreach (var value in values.Where(v => !target.Contains(v)))
                    target.Add(value);
            }

            foreach (var tag in entry.Tags.Where(t => !result.Tags.Contains(t)))
            {
                if (result.Tags.Count >= MaxTags) break;
                result.Tags.Add(tag);
            }
        }
    }

    private void FillSentences(DictionaryResult result, IEnumerable<WordEntry> entries)
    {
        var max = Math.Max(0, _settings.MaxExamples);
        if (max == 0)
            return;

        // 先每个义项取一句，覆盖更多义项；不够再按顺序补
        var perSense = entries
            .SelectMany(e => e.Senses.Select(s => (Label: PartOfSpeech.Short(e.PartOfSpeech) + s.Number, Sense: s)))
            .Where(x => x.Sense.Examples.Count > 0)
            .ToList();
        var picked = perSense.Select(x => (x.Label, Example: x.Sense.Examples[0]))
            .Concat(perSense.SelectMany(x => x.Sense.Examples.Skip(1).Select(ex => (x.Label, Example: ex))))
            .Take(max);

        foreach (var (label, example) in picked)
        {
            var prefix = label.Length > 0 ? $"[{label}] " : string.Empty;
            result.Sentences.Add(example.Translation == null
                ? prefix + example.Text
                : $"{prefix}{example.Text}\n{example.Translation}");
        }
    }

    private string AudioUrl(string dictionaryId, string? path)
    {
        if (path == null || _library.FindById(dictionaryId)?.HasResource(path) != true)
            return string.Empty;
        try
        {
            return _audio.BuildUrl(dictionaryId, path);
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(ex, "本地发音服务启动失败");
            return string.Empty;
        }
    }

    private static DictMean Row(string label, string text) =>
        new() { PartOfSpeech = label, Means = [text] };

    #endregion
}
