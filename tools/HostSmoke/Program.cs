using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using STranslate.Plugin;

// 模拟 STranslate 2.0.10 的插件加载与调用流程，验证插件在宿主环境下可用。
// 用法：HostSmoke <插件目录> <mdx 路径> <设置界面截图.png> [词...]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.ThemeResources());
        app.Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.Controls.XamlControlsResources());
        var exitCode = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args[0] == "preview")
                    await Preview.RunAsync(args[1], args[2], args[3], args[4..]);
                else
                    await RunAsync(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex);
                exitCode = 1;
            }
            app.Shutdown(exitCode);
        };
        return app.Run();
    }

    private static async Task RunAsync(string[] args)
    {
        var pluginDir = args[0];
        var mdx = args[1];
        var screenshot = args[2];
        var words = args.Length > 3 ? args[3..] : ["nausea", "running", "went", "look up", "record", "zzzzqx", "a whole sentence that is way too long to be a dictionary word"];

        // 与宿主一致：先加载语言资源，再用独立的 AssemblyLoadContext 加载插件
        var lang = Path.Combine(pluginDir, "Languages", "zh-cn.xaml");
        using (var fs = File.OpenRead(lang))
            Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(fs));

        _ = new DictionaryResult(); // 确保共享程序集已在默认上下文中
        var meta = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(pluginDir, "plugin.json")));
        var dllPath = Path.Combine(pluginDir, meta.GetProperty("ExecuteFileName").GetString()!);
        var loader = new PluginLoader(dllPath);
        var assembly = loader.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(dllPath)));
        var type = assembly.ExportedTypes.First(t => t.IsClass && !t.IsAbstract && t.GetInterfaces().Contains(typeof(IPlugin)));
        Console.WriteLine($"plugin type: {type.FullName}, ALC: {AssemblyLoadContext.GetLoadContext(assembly)?.GetType().Name}");
        Console.WriteLine($"HtmlAgilityPack from: {loader.Assemblies.FirstOrDefault(a => a.GetName().Name == "HtmlAgilityPack")?.Location ?? "(not yet loaded)"}");

        var plugin = (IDictionaryPlugin)Activator.CreateInstance(type)!;
        var context = new FakeContext(mdx);
        var sw = Stopwatch.StartNew();
        plugin.Init(context);
        Console.WriteLine($"Init: {sw.ElapsedMilliseconds} ms");

        string? firstAudio = null;
        foreach (var word in words)
        {
            var result = new DictionaryResult();
            sw.Restart();
            try
            {
                await plugin.TranslateAsync(word, result, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n#### {word}: EXCEPTION {ex.Message}");
                continue;
            }
            Console.WriteLine($"\n#### {word}: {result.ResultType} \"{result.Text}\" ({sw.ElapsedMilliseconds} ms)");
            foreach (var s in result.Symbols)
            {
                Console.WriteLine($"  [{s.Label}] /{s.Phonetic}/ {s.AudioUrl}");
                firstAudio ??= string.IsNullOrEmpty(s.AudioUrl) ? null : s.AudioUrl;
            }
            foreach (var m in result.DictMeans.Take(6))
                Console.WriteLine($"  {m.PartOfSpeech,-8} {string.Join("；", m.Means)}");
            if (result.DictMeans.Count > 6) Console.WriteLine($"  … {result.DictMeans.Count} rows");
            void Forms(string name, ObservableCollection<string> c) { if (c.Count > 0) Console.WriteLine($"  {name}: {string.Join(", ", c)}"); }
            Forms("复数", result.Plurals); Forms("过去式", result.PastTense); Forms("过去分词", result.PastParticiple);
            Forms("现在分词", result.PresentParticiple); Forms("三单", result.ThirdPersonSingular);
            Forms("比较级", result.Comparative); Forms("最高级", result.Superlative); Forms("标签", result.Tags);
            foreach (var s in result.Sentences.Take(3))
                Console.WriteLine($"  例: {s.Replace("\n", " / ")}");
        }

        // 发音：宿主通过 HTTP 下载 AudioUrl 再交给 NAudio
        if (firstAudio != null)
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = true });
            var bytes = await http.GetByteArrayAsync(firstAudio);
            Console.WriteLine($"\naudio GET {bytes.Length} bytes, mp3 sync: {bytes.Length > 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0 || bytes.AsSpan(0, 3).SequenceEqual("ID3"u8)}");
        }

        // 设置界面：按宿主方式取得并渲染
        var ui = plugin.GetSettingUI();
        var window = new Window { Width = 760, Height = 900, Content = new ScrollViewer { Content = ui }, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -3000, Top = 0 };
        window.Show();
        await Task.Delay(1500); // 等词库状态异步载入
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var png = File.Create(screenshot))
            encoder.Save(png);
        window.Close();
        Console.WriteLine($"settings UI rendered: {screenshot}; saved settings: {context.SavedJson}");

        plugin.Dispose();
    }
}

internal sealed class PluginLoader(string path) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(path);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var existing = Default.Assemblies.FirstOrDefault(x => x.FullName == assemblyName.FullName);
        if (existing != null) return existing;
        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolved == null ? null : LoadFromAssemblyPath(resolved);
    }
}

internal sealed class FakeContext(string mdx) : IPluginContext
{
    private readonly Dictionary<Type, object> _storage = [];
    public string SavedJson { get; private set; } = string.Empty;

    public PluginMetaData MetaData { get; } = new();
    public ILogger Logger { get; } = NullLogger.Instance;
    public IHttpService HttpService => throw new NotSupportedException("插件不应联网");
    public IAudioPlayer AudioPlayer => throw new NotSupportedException();
    public ISnackbar Snackbar => throw new NotSupportedException();
    public INotification Notification => throw new NotSupportedException();
    public ImageQuality ImageQuality => default;

    public string GetTranslation(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    public Window GetPromptEditWindow(ObservableCollection<Prompt> prompts, List<string>? roles = null) => throw new NotSupportedException();

    public T LoadSettingStorage<T>() where T : new()
    {
        if (_storage.TryGetValue(typeof(T), out var existing))
            return (T)existing;
        // 模拟已有设置：词库列表里放一本
        var json = $$"""{"Dictionaries":[{"Path":{{JsonSerializer.Serialize(mdx)}},"Enabled":true}]}""";
        var value = JsonSerializer.Deserialize<T>(json) ?? new T();
        _storage[typeof(T)] = value;
        return value;
    }

    public void SaveSettingStorage<T>() where T : new() =>
        SavedJson = JsonSerializer.Serialize(_storage[typeof(T)]);

    public void ApplyTheme(Window window) { }

    public void Dispose() { }
}
