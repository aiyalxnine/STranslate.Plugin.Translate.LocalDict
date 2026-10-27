using System.Net;
using System.Text;
using STranslate.Plugin.Translate.LocalDict.Core;
using STranslate.Plugin.Translate.LocalDict.Mdict;

/// <summary>
/// 不依赖真实词库的自动测试：用 MdictWriter 生成合成词库，覆盖读取、解析、查询回退和本地发音服务。
/// 词条内容均为自拟，只模仿朗文 MDX 的标记结构（包括不闭合的标签和弹出层）。
/// </summary>
internal static class SelfTest
{
    private static int _passed;
    private static readonly List<string> Failures = [];

    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "localdict-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var mdx = Path.Combine(dir, "test.mdx");
            MdictWriter.Write(mdx, Fixtures.Entries.Select(e => (e.Key, Encoding.UTF8.GetBytes(e.Value))), resource: false, title: "Self Test");
            MdictWriter.Write(Path.ChangeExtension(mdx, ".mdd"), Fixtures.Audio.Select(a => (a, FakeMp3(a))), resource: true);

            var utf16 = Path.Combine(dir, "utf16.mdx");
            MdictWriter.Write(utf16, [("alpha", Encoding.Unicode.GetBytes("<b>alpha</b><br>第一个字母")), ("beta", Encoding.Unicode.GetBytes("<b>beta</b><br>第二个字母"))],
                resource: false, encoding: "UTF-16", encryptKeyInfo: false, recordCompression: MdictWriter.BlockCompression.LzoLiteral, entriesPerBlock: 1);

            ReaderTests(mdx, utf16);
            using var dict = LoadedDictionary.Open(mdx);
            ParserTests(dict);
            FallbackTests(dict);
            AudioServerTests(dict);
            UnitTests();
        }
        catch (Exception ex)
        {
            Failures.Add("未预期的异常：" + ex);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录清理失败不影响结果 */ }
        }

        Console.WriteLine($"\n{_passed} passed, {Failures.Count} failed");
        Failures.ForEach(f => Console.WriteLine("  FAIL " + f));
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void ReaderTests(string mdx, string utf16)
    {
        using var file = new MdictFile(mdx);
        Check("加密索引的 MDX 可以打开", file.Count == Fixtures.Entries.Count, $"Count={file.Count}");
        Check("读取标题", file.Title == "Self Test", file.Title);

        using var resources = new MdictFile(Path.ChangeExtension(mdx, ".mdd"));
        Check("MDD 资源数", resources.Count == Fixtures.Audio.Length, $"Count={resources.Count}");

        using var u16 = new MdictFile(utf16);
        Check("UTF-16 键 + LZO 数据块", u16.Count == 2 && u16.ReadText(0).Contains("第一个字母"), u16.Count > 0 ? u16.ReadText(0) : "空");
    }

    private static void ParserTests(LoadedDictionary dict)
    {
        Check("资源路径大小写和斜杠无关", dict.HasResource("HWD/BRE/R/record_n.mp3"));

        var record = DictionaryLibrary.LookupIn(dict, "record");
        Check("同形异义词拆成两个词条，弹出层里的词条被忽略", record.Count == 2, string.Join(",", record.Select(e => e.Headword + "/" + e.PartOfSpeech)));
        if (record.Count < 2) return;

        var noun = record[0];
        Check("名词英音", Pron(noun, "英") == "ˈrekɔːd", Pron(noun, "英"));
        Check("美音只写结尾时补全", Pron(noun, "美") == "ˈrekərd", Pron(noun, "美"));
        Check("英音发音路径", noun.Pronunciations[0].AudioPath == "hwd/bre/r/record_n.mp3", noun.Pronunciations[0].AudioPath);
        Check("词头标签不含义项里的标签", noun.Tags.SequenceEqual(["S1"]), string.Join(",", noun.Tags));
        Check("义项编号含子义项", noun.Senses.Select(s => s.Number).SequenceEqual(["1", "2a", "2b"]), string.Join(",", noun.Senses.Select(s => s.Number)));

        var s1 = noun.Senses[0];
        Check("路标与语法码", s1.Labels.SequenceEqual(["〔信息〕", "[C]"]), string.Join(" ", s1.Labels));
        Check("例句之后的标签不算义项标签", !s1.Labels.Any(l => l.Contains("example")), string.Join(" ", s1.Labels));
        Check("中文释义与标点规范化", s1.Translation == "记录，记载", s1.Translation);
        Check("英文释义", s1.Definition == "information that is written down", s1.Definition);
        Check("例句与译文", s1.Examples.Count == 1 && s1.Examples[0].Text == "Keep a record of what you spend." && s1.Examples[0].Translation == "把花销记下来。",
            s1.Examples.FirstOrDefault()?.ToString());
        Check("例句发音在 MDD 中", s1.Examples.Count == 1 && dict.HasResource(s1.Examples[0].AudioPath!));
        Check("父义项路标归入第一个子义项", noun.Senses[1].Labels.Contains("〔最好〕"), string.Join(" ", noun.Senses[1].Labels));
        Check("语体标签中文化", noun.Senses[2].Labels.Contains("〈非正式〉"), string.Join(" ", noun.Senses[2].Labels));

        var verb = record[1];
        Check("动词读音", Pron(verb, "英") == "rɪˈkɔːd" && Pron(verb, "美") == "rɪˈkɔːrd", Pron(verb, "英") + " / " + Pron(verb, "美"));
        Check("没有美音链接时不填发音路径", verb.Pronunciations[1].AudioPath == null);
        Check("动词语法码", verb.Senses.Count == 1 && verb.Senses[0].Labels.SequenceEqual(["[T]"]), string.Join(" ", verb.Senses.SelectMany(s => s.Labels)));

        var go = DictionaryLibrary.LookupIn(dict, "go");
        var goVerb = go.FirstOrDefault();
        Check("不规则词形", goVerb != null && goVerb.Forms.GetValueOrDefault(WordForm.PastTense)?.SequenceEqual(["went"]) == true
                         && goVerb.Forms.GetValueOrDefault(WordForm.PastParticiple)?.SequenceEqual(["gone"]) == true
                         && goVerb.Forms.GetValueOrDefault(WordForm.ThirdPersonSingular)?.SequenceEqual(["goes"]) == true);
        Check("短语动词列表", goVerb?.PhrasalVerbs.SequenceEqual(["go something out"]) == true, string.Join(";", goVerb?.PhrasalVerbs ?? []));
        Check("短语动词的义项不混进主词条", goVerb?.Senses.Count == 1, $"{goVerb?.Senses.Count}");

        var hello = DictionaryLibrary.LookupIn(dict, "hello").FirstOrDefault();
        Check("多个词性", hello?.PartOfSpeech == "interjection, noun", hello?.PartOfSpeech);
        Check("美音中段/结尾替换补全", hello != null && Pron(hello, "美") == "həˈloʊ", hello == null ? "" : Pron(hello, "美"));
        Check("地域标签中文化", hello?.Senses.FirstOrDefault()?.Labels.Contains("〈英式〉") == true);
    }

    private static void FallbackTests(LoadedDictionary dict)
    {
        var went = DictionaryLibrary.LookupIn(dict, "went");
        Check("变形词条补上原形词条", went.Select(e => e.Headword).SequenceEqual(["went", "go"]), string.Join(",", went.Select(e => e.Headword)));

        var happier = DictionaryLibrary.LookupIn(dict, "happier");
        Check("词形还原 happier → happy", happier.FirstOrDefault()?.Headword == "happy");
        Check("比较级", happier.FirstOrDefault()?.Forms.GetValueOrDefault(WordForm.Comparative)?.Contains("happier") == true);

        Check("词形还原 going → go", DictionaryLibrary.LookupIn(dict, "going").FirstOrDefault()?.Headword == "go");

        var goOut = DictionaryLibrary.LookupIn(dict, "go out");
        Check("短语动词（去掉宾语后匹配）", goOut.Count == 1 && goOut[0].Headword == "go something out" && goOut[0].Senses[0].Translation == "出门",
            string.Join(",", goOut.Select(e => e.Headword)));
        Check("不规则动词的短语动词 went out → go out", DictionaryLibrary.LookupIn(dict, "went out").FirstOrDefault()?.Headword == "go something out");

        var colour = DictionaryLibrary.LookupIn(dict, "colour");
        Check("@@@LINK 跳转 + 通用解析", colour.Count == 1 && colour[0].Senses.Select(s => s.Translation).SequenceEqual(["颜色", "the quality of being red, blue etc"]),
            string.Join(" | ", colour.SelectMany(e => e.Senses).Select(s => s.Translation)));
        Check("跳转循环不会卡死", DictionaryLibrary.LookupIn(dict, "loop-a").Count == 0);
        Check("忽略大小写和两侧标点", DictionaryLibrary.LookupIn(dict, DictionaryLibrary.NormalizeQuery(" “RECORD”. ")!).Count == 2);
        Check("查不到时返回空", DictionaryLibrary.LookupIn(dict, "zzzzqx").Count == 0);
        Check("句子不当作查词", DictionaryLibrary.NormalizeQuery("this is a whole sentence that is far too long") == null);
    }

    private static void AudioServerTests(LoadedDictionary dict)
    {
        using var server = new AudioServer((id, path) => id == dict.Id ? dict.ReadResource(path) : null, 0, "token123");
        var url = server.BuildUrl(dict.Id, "hwd/bre/r/record_n.mp3");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        Check("本地发音服务返回 MDD 中的音频", bytes.SequenceEqual(FakeMp3("\\hwd\\bre\\r\\record_n.mp3")));
        Check("令牌错误返回 404", http.GetAsync(url.Replace("token123", "wrong")).GetAwaiter().GetResult().StatusCode == HttpStatusCode.NotFound);
        Check("不存在的资源返回 404", http.GetAsync(url.Replace("record_n", "missing")).GetAwaiter().GetResult().StatusCode == HttpStatusCode.NotFound);
        Check("只监听本机", url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), url);
    }

    private static void UnitTests()
    {
        foreach (var (bre, ame, expected) in new[]
                 {
                     ("həˈləʊ, he-", "-ˈloʊ", "həˈloʊ"),
                     ("rɪˈkɔːd", "-ɔːrd", "rɪˈkɔːrd"),
                     ("ˈrekɔːd", "-ərd", "ˈrekərd"),
                     ("bɪˈtrəʊðəl", "-ˈtroʊ-", "bɪˈtroʊðəl"),
                     ("ˌæntɪbaɪˈɒtɪk◂", "-ˈɑː-", "ˌæntɪbaɪˈɑːtɪk◂"),
                     ("ˈɔːkni", "ˈɔːr-", "ˈɔːrkni"),
                     ("ˈpɒstʃərɪŋ", "ˈpɑːs-", "ˈpɑːstʃərɪŋ"),
                     ("ˌes əʊ ˈes", "-oʊ-", "-oʊ-"),
                     ("ˈrʌsəl, ˈbɜːtrənd", "ˈbɜːr-", "ˈbɜːr-"),
                     ("ˈnɔːziə", "ˈnɒːziə, -ʃə", "ˈnɒːziə, -ʃə")
                 })
        {
            var actual = LongmanParser.ExpandPartialPron(bre, ame);
            Check($"美音补全 {bre} + {ame}", actual == expected, actual);
        }

        Check("语法码缩写", Labels.Grammar("[intransitive, transitive]") == "[I, T]" && Labels.Grammar("[countable usually plural]") == "[C usually plural]");
        Check("语体标签", Labels.RegisterText("especially British English") == "尤英式" && Labels.RegisterText("trademark") == "商标" && Labels.RegisterText("unknown") == "unknown");
        Check("词性缩写", PartOfSpeech.Short("interjection, noun") == "int." && PartOfSpeech.Abbreviate("interjection, noun") == "int./n.");
        Check("词形还原候选", Lemmatizer.Candidates("studies").Contains("study") && Lemmatizer.Candidates("stopped").Contains("stop") && Lemmatizer.Candidates("bigger").Contains("big"));
        Check("RIPEMD-128 测试向量", Convert.ToHexStringLower(Ripemd128.Hash("abc"u8)) == "c14a12199c66e4ba84636b0f69144c77");
    }

    private static string Pron(WordEntry entry, string label) =>
        entry.Pronunciations.FirstOrDefault(p => p.Label == label)?.Phonetic ?? "(无)";

    private static byte[] FakeMp3(string key) => [0xFF, 0xF3, 0x44, 0xC4, .. Encoding.UTF8.GetBytes(key)];

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  ok   " + name);
        }
        else
        {
            Failures.Add(detail == null ? name : $"{name}（实际：{detail}）");
            Console.WriteLine("  FAIL " + name + (detail == null ? "" : $"（实际：{detail}）"));
        }
    }
}

/// <summary>自拟的测试词条，结构模仿朗文 MDX。</summary>
internal static class Fixtures
{
    public static readonly string[] Audio =
    [
        "\\hwd\\bre\\r\\record_n.mp3", "\\hwd\\ame\\r\\record_n_am.mp3", "\\hwd\\bre\\r\\record_v.mp3",
        "\\exa\\bre\\1\\rec1.mp3", "\\hwd\\bre\\g\\go.mp3", "\\hwd\\ame\\g\\go.mp3"
    ];

    // record：词头的 span 故意不闭合（义项因此嵌在 entryhead/buttons 里），并带一个应被忽略的弹出层词条
    private const string Record =
        """<link rel="stylesheet" href="t.css"><div><span class="entry"><span class="entryhead"><span class="hwd">record</span><span class="homnum">1</span> """ +
        """<span class="proncodes"><span class="neutral">/</span><span class="pron">ˈrekɔːd</span> <span class="amevarpron"><span class="neutral">$</span> -ərd</span><span class="neutral">/</span></span> """ +
        """<span class="freq">S1</span> <span class="pos">noun</span> <a href="sound://hwd/bre/r/record_n.mp3"><img src="a.png"></a> <a href="sound://hwd/ame/r/record_n_am.mp3"><img src="b.png"></a>""" +
        """<span class="buttons"><span class="popup-button">Origin</span><div class="at-link"><span class="entry"><span class="entryhead"><span class="hwd">popup</span></span>""" +
        """<span class="sense"><span class="def"><EN>should be ignored</EN></span></span></span></div>""" +
        """<span class="sense"><span class="sensenum">1</span><span class="signpost"><SIGNEN>information</SIGNEN><SIGN>信息</SIGN></span> <span class="gram">[countable]</span> """ +
        """<span class="def"><EN>information that is written down</EN><TRAN>记录﹐记载</TRAN></span>""" +
        """<span class="example"><EXAEN><a href="sound://exa/bre/1/rec1.mp3"><img src="c.png"></a> Keep a record of what you spend.</EXAEN><EXAMPLE>把花销记下来。</EXAMPLE></span> """ +
        """<span class="registerlab">example only</span></span>""" +
        """<span class="sense"><span class="sensenum">2</span><span class="signpost"><SIGNEN>best</SIGNEN><SIGN>最好</SIGN></span>""" +
        """<span class="subsense"><span class="sensenum">a)</span><span class="def"><EN>the best result ever achieved</EN><TRAN>纪录</TRAN></span></span>""" +
        """<span class="subsense"><span class="sensenum">b)</span><span class="registerlab">informal</span> <span class="def"><EN>a very good result</EN><TRAN>好成绩</TRAN></span></span></span>""" +
        """</div>""" +
        """<div><span class="entry"><span class="entryhead"><span class="hwd">record</span><span class="homnum">2</span> <span class="proncodes"><span class="pron">rɪˈkɔːd</span> <span class="amevarpron">$ -ɔːrd</span></span> """ +
        """<span class="pos">verb</span> <a href="sound://hwd/bre/r/record_v.mp3"></a></span>""" +
        """<span class="sense"><span class="gram">[transitive]</span> <span class="def"><EN>to write information down</EN><TRAN>记录</TRAN></span></span></span></div>""";

    private const string Go =
        """<span class="entry"><span class="entryhead"><span class="hwd">go</span> <span class="proncodes"><span class="pron">ɡəʊ</span> <span class="amevarpron">$ ɡoʊ</span></span> <span class="pos">verb</span> """ +
        """<span class="inflections">(<span class="pasttense"><span class="infllab">past tense</span> went</span>, <span class="pastpart"><span class="infllab">past participle</span> gone</span>, """ +
        """<span class="t3perssing"><span class="infllab">third person singular</span> goes</span>)</span> <a href="sound://hwd/bre/g/go.mp3"></a> <a href="sound://hwd/ame/g/go.mp3"></a></span>""" +
        """<span class="sense"><span class="sensenum">1</span><span class="def"><EN>to move to a place</EN><TRAN>去</TRAN></span></span>""" +
        """<span class="phrvbentry"><span class="entryhead"><span class="phrvbhwd">go <span class="object">something</span> out</span> <span class="pos">phrasal verb</span></span>""" +
        """<span class="sense"><span class="def"><EN>to leave your house</EN><TRAN>出门</TRAN></span></span></span></span>""";

    private const string Went =
        """<span class="entry"><span class="entryhead"><span class="hwd">went</span> <span class="proncodes"><span class="pron">went</span></span> <span class="pos">verb</span></span>""" +
        """<span class="sense"><span class="def"><EN>the past tense of <span class="nondv"><span class="refhwd">go</span></span></EN><TRAN>go的过去式</TRAN></span></span></span>""";

    private const string Happy =
        """<span class="entry"><span class="entryhead"><span class="hwd">happy</span> <span class="proncodes"><span class="pron">ˈhæpi</span></span> <span class="pos">adjective</span> """ +
        """<span class="inflections">(<span class="comp"><span class="infllab">comparative</span> happier</span>, <span class="superl"><span class="infllab">superlative</span> happiest</span>)</span></span>""" +
        """<span class="sense"><span class="def"><EN>feeling pleasure</EN><TRAN>快乐的</TRAN></span></span></span>""";

    private const string Hello =
        """<span class="entry"><span class="entryhead"><span class="hwd">hello</span> <span class="proncodes"><span class="pron">həˈləʊ, he-</span> <span class="amevarpron">$ -ˈloʊ</span></span> """ +
        """<span class="pos">interjection</span>, <span class="pos">noun</span></span>""" +
        """<span class="sense"><span class="geo">British English</span> <span class="def"><EN>used to show surprise</EN><TRAN>嘿</TRAN></span></span></span>""";

    public static readonly Dictionary<string, string> Entries = new()
    {
        ["record"] = Record,
        ["go"] = Go,
        ["went"] = Went,
        ["happy"] = Happy,
        ["hello"] = Hello,
        ["color"] = "<b>color</b><br>颜色<br>the quality of being red, blue etc",
        ["colour"] = "@@@LINK=color",
        ["loop-a"] = "@@@LINK=loop-b",
        ["loop-b"] = "@@@LINK=loop-a"
    };
}
