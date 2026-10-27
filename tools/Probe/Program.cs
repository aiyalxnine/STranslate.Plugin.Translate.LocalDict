using System.Diagnostics;
using System.Text;
using STranslate.Plugin.Translate.LocalDict.Core;
using STranslate.Plugin.Translate.LocalDict.Mdict;

Console.OutputEncoding = Encoding.UTF8;
var mode = args.Length > 0 ? args[0] : "selftest";

// 不依赖真实词库的自动测试（CI 使用）
if (mode == "selftest")
{
    Environment.ExitCode = SelfTest.Run();
    return;
}

if (mode == "ripemd")
{
    Console.WriteLine(Convert.ToHexStringLower(Ripemd128.Hash([])));
    Console.WriteLine(Convert.ToHexStringLower(Ripemd128.Hash("abc"u8)));
    Console.WriteLine(Convert.ToHexStringLower(Ripemd128.Hash(Encoding.ASCII.GetBytes(new string('a', 1_000_000)))));
    return;
}

if (mode == "parse")
{
    var swp = Stopwatch.StartNew();
    using var loaded = LoadedDictionary.Open(args[1]);
    Console.WriteLine($"{loaded.Title}: {loaded.EntryCount} entries, {loaded.ResourceCount} resources, opened+indexed in {swp.ElapsedMilliseconds} ms");
    foreach (var word in args.Skip(2))
    {
        swp.Restart();
        var entries = DictionaryLibrary.LookupIn(loaded, DictionaryLibrary.NormalizeQuery(word)!);
        Console.WriteLine($"\n######## {word}  ({entries.Count} entries, {swp.ElapsedMilliseconds} ms)");
        foreach (var e in entries)
        {
            Console.WriteLine($"== {e.Headword} | {e.PartOfSpeech} | tags: {string.Join(", ", e.Tags)}");
            foreach (var p in e.Pronunciations)
                Console.WriteLine($"   [{p.Label}] /{p.Phonetic}/  {p.AudioPath} {(p.AudioPath != null && loaded.HasResource(p.AudioPath) ? "✓" : "✗")}");
            foreach (var (form, values) in e.Forms)
                Console.WriteLine($"   {form}: {string.Join(", ", values)}");
            foreach (var s in e.Senses.Take(args.Length > 3 ? 8 : 40))
            {
                Console.WriteLine($"   {s.Number,-4} {string.Join(" ", s.Labels)} {s.Translation} ‖ {s.Definition}");
                foreach (var ex in s.Examples.Take(2))
                    Console.WriteLine($"          · {ex.Text} / {ex.Translation}  {(ex.AudioPath != null && loaded.HasResource(ex.AudioPath) ? "♪" : "")}");
            }
            if (e.Senses.Count > 8) Console.WriteLine($"   … {e.Senses.Count} senses total");
            if (e.PhrasalVerbs.Count > 0) Console.WriteLine($"   phrasal verbs: {string.Join("; ", e.PhrasalVerbs)}");
        }
    }
    return;
}

if (mode == "prons")
{
    foreach (var (b, a) in new[] { ("həˈləʊ, he-", "-ˈloʊ"), ("rɪˈkɔːd", "-ɔːrd"), ("ˈrekɔːd", "-ərd"), ("ˈnɔːziə, -siə", "ˈnɒːziə, -ʃə"), ("ˈwɔːtə", "ˈwɒːtər, ˈwɑː-"),
                 ("bɪˈtrəʊðəl", "bɪˈtroʊ-"), ("ˌæntɪbaɪˈɒtɪk◂", "ˌæntɪbaɪˈɑː-"), ("ˈpɒstʃərɪŋ", "ˈpɑːs-"), ("ˈɔːkni", "ˈɔːr-"), ("ˈzɪmə ˌfreɪm", "ˈzɪmər-"),
                 ("ˌes əʊ ˈes", "-oʊ-"), ("ˌæntɪbaɪˈɒtɪk◂", "-ˈɑː-"), ("bɪˈtrəʊðəl", "-ˈtroʊ-"), ("ˈzɪmə ˌfreɪm", "-mər-"), ("ˌprezbəˈtɪəriən◂", "-ˈtɪr-"), ("ˌbuːtrɒs ˈɡɑːli", "-trəʊs-"), ("ˈdeməɡɒɡ", "-ɡɑːɡ"), ("ˈrʌsəl, ˈbɜːtrənd", "ˈbɜːr-"), ("ˌbuːtrɒs ˈɡɑːli", "ˌbuːtrəʊs-"), ("ˈɔːtəmeɪt", "ˈɒː-") })
        Console.WriteLine($"{b}  +  {a}  =>  {LongmanParser.ExpandPartialPron(b, a)}");
    using var loaded = LoadedDictionary.Open(args[1]);
    var mdx = new MdictFile(args[1]);
    var rnd = new Random(7);
    var shown = 0;
    for (var k = 0; k < 4000 && shown < 45; k++)
    {
        var q = DictionaryLibrary.NormalizeQuery(mdx.GetKey(rnd.Next(mdx.Count)));
        if (q == null) continue;
        foreach (var e in DictionaryLibrary.LookupIn(loaded, q))
        {
            if (e.Pronunciations.Count < 2) continue;
            var (bre, ame) = (e.Pronunciations[0].Phonetic, e.Pronunciations[1].Phonetic);
            if (bre == ame) continue;
            Console.WriteLine($"{e.Headword,-22} 英 /{bre}/   美 /{ame}/");
            shown++;
        }
    }
    return;
}

if (mode == "mem")
{
    GC.Collect();
    var before = GC.GetTotalMemory(true);
    var swm = Stopwatch.StartNew();
    using var loaded = LoadedDictionary.Open(args[1]);
    var elapsed = swm.ElapsedMilliseconds;
    GC.Collect();
    var after = GC.GetTotalMemory(true);
    Console.WriteLine($"open+index {elapsed} ms, managed heap +{(after - before) / 1024 / 1024} MB, entries {loaded.EntryCount}, resources {loaded.ResourceCount}");
    GC.KeepAlive(loaded);
    return;
}

if (mode == "bulk")
{
    using var loaded = LoadedDictionary.Open(args[1]);
    var mdx = new MdictFile(args[1]);
    var rnd = new Random(42);
    int n = int.Parse(args[2]), empty = 0, failed = 0, noSense = 0;
    long worst = 0; string worstWord = "";
    var total = Stopwatch.StartNew();
    for (var k = 0; k < n; k++)
    {
        var key = mdx.GetKey(rnd.Next(mdx.Count));
        var q = DictionaryLibrary.NormalizeQuery(key);
        if (q == null) continue;
        var sw1 = Stopwatch.StartNew();
        try
        {
            var entries = DictionaryLibrary.LookupIn(loaded, q);
            if (entries.Count == 0) { empty++; if (empty <= 15) Console.WriteLine($"EMPTY: {key}"); }
            else if (entries.All(e => e.Senses.Count == 0)) { noSense++; if (noSense <= 15) Console.WriteLine($"NOSENSE: {key}"); }
        }
        catch (Exception ex) { failed++; Console.WriteLine($"FAIL: {key}: {ex.GetType().Name} {ex.Message}"); }
        if (sw1.ElapsedMilliseconds > worst) { worst = sw1.ElapsedMilliseconds; worstWord = key; }
    }
    Console.WriteLine($"{n} lookups in {total.ElapsedMilliseconds} ms; empty={empty} noSense={noSense} failed={failed}; worst {worst} ms ({worstWord})");
    return;
}

if (mode == "debug")
{
    var doc = new HtmlAgilityPack.HtmlDocument();
    doc.LoadHtml(File.ReadAllText(args[1]));
    var senses = doc.DocumentNode.Descendants().Where(n => LongmanParser.HasClass(n, "sense")).ToList();
    Console.WriteLine($"senses: {senses.Count}");
    foreach (var s in senses.Take(3))
    {
        var chain = new List<string>();
        for (var p = s.ParentNode; p != null; p = p.ParentNode)
            chain.Add($"{p.Name}.{p.GetAttributeValue("class", "")}");
        Console.WriteLine(string.Join(" < ", chain));
    }
    return;
}

if (mode == "audio")
{
    using var loaded = LoadedDictionary.Open(args[1]);
    using var server = new AudioServer((id, path) => id == loaded.Id ? loaded.ReadResource(path) : null, 0, Guid.NewGuid().ToString("N"));
    var url = server.BuildUrl(loaded.Id, args[2]);
    Console.WriteLine(url);
    using var http = new HttpClient();
    var bytes = await http.GetByteArrayAsync(url);
    Console.WriteLine($"GET -> {bytes.Length} bytes, head {Convert.ToHexString(bytes.AsSpan(0, 4))}");
    var bad = await http.GetAsync(url.Replace(server.Token, "wrongtoken"));
    Console.WriteLine($"wrong token -> {(int)bad.StatusCode}");
    return;
}

var sw = Stopwatch.StartNew();
using var dict = new MdictFile(args[1]);
Console.WriteLine($"{dict.Title}: {dict.Count} entries, loaded in {sw.ElapsedMilliseconds} ms, encoding {dict.TextEncoding.WebName}");

if (mode == "keys")
{
    var skip = args.Length > 2 ? int.Parse(args[2]) : 0;
    for (var i = skip; i < Math.Min(dict.Count, skip + 40); i++)
        Console.WriteLine($"[{i}] {dict.GetKey(i)}");
}
else if (mode == "ext")
{
    var groups = Enumerable.Range(0, dict.Count).Select(dict.GetKey)
        .GroupBy(k => Path.GetExtension(k).ToLowerInvariant())
        .OrderByDescending(g => g.Count());
    foreach (var g in groups.Take(15))
        Console.WriteLine($"{g.Key}: {g.Count()}  e.g. {string.Join(" | ", g.Take(4))}");
}
else if (mode == "find")
{
    var pattern = args[2];
    var hits = Enumerable.Range(0, dict.Count).Where(i => dict.GetKey(i).Contains(pattern, StringComparison.OrdinalIgnoreCase)).Take(40);
    foreach (var i in hits)
        Console.WriteLine($"[{i}] {dict.GetKey(i)}  ({dict.ReadRecord(i).Length} bytes)");
}
else if (mode == "show")
{
    var word = args[2];
    var max = args.Length > 3 ? int.Parse(args[3]) : 20000;
    sw.Restart();
    foreach (var i in Enumerable.Range(0, dict.Count).Where(i => dict.GetKey(i).Equals(word, StringComparison.OrdinalIgnoreCase)))
    {
        var text = dict.ReadText(i);
        Console.WriteLine($"===== [{i}] {dict.GetKey(i)} ({text.Length} chars, {sw.ElapsedMilliseconds} ms)");
        Console.WriteLine(text.Length > max ? text[..max] + " …" : text);
    }
}
else if (mode == "cp")
{
    string Cp(string s) => string.Join(" ", s.Select(c => ((int)c).ToString("X4")));
    var target = args[2];
    foreach (var i in Enumerable.Range(0, dict.Count).Where(i => dict.GetKey(i).Normalize(NormalizationForm.FormD).Contains(target.Normalize(NormalizationForm.FormD), StringComparison.OrdinalIgnoreCase)).Take(20))
    {
        var text = dict.ReadText(i);
        Console.WriteLine($"[{i}] key={Cp(dict.GetKey(i))} len={text.Length} {(text.StartsWith("@@@") ? "→ " + Cp(text[8..].Trim()) : "")}");
    }
}
else if (mode == "dump")
{
    var key = args[2];
    var outPath = args[3];
    var i = Enumerable.Range(0, dict.Count).First(i => dict.GetKey(i).Equals(key, StringComparison.OrdinalIgnoreCase));
    var bytes = dict.ReadRecord(i);
    File.WriteAllBytes(outPath, bytes);
    Console.WriteLine($"{bytes.Length} bytes, head {Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length)))}");
}
