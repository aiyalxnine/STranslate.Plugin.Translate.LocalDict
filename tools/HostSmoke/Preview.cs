using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iNKORE.UI.WPF.Modern;
using STranslate.Plugin;

/// <summary>
/// 按 STranslate 2.0.10 OutputControl.xaml 中 DictionaryPluginTemplate（成功结果部分）的结构与尺寸复刻渲染，
/// 用真实查询结果输出截图，检查排版。用法：HostSmoke preview &lt;插件目录&gt; &lt;mdx&gt; &lt;输出目录&gt; 词...
/// </summary>
internal static class Preview
{
    private const double WindowWidth = 470;

    public static async Task RunAsync(string pluginDir, string mdx, string outDir, string[] words)
    {
        Directory.CreateDirectory(outDir);
        var lang = Path.Combine(pluginDir, "Languages", "zh-cn.xaml");
        using (var fs = File.OpenRead(lang))
            Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(fs));

        _ = new DictionaryResult();
        var meta = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(pluginDir, "plugin.json")));
        var dllPath = Path.Combine(pluginDir, meta.GetProperty("ExecuteFileName").GetString()!);
        var loader = new PluginLoader(dllPath);
        var assembly = loader.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(dllPath)));
        var type = assembly.ExportedTypes.First(t => t.IsClass && !t.IsAbstract && t.GetInterfaces().Contains(typeof(IPlugin)));
        var plugin = (IDictionaryPlugin)Activator.CreateInstance(type)!;
        plugin.Init(new FakeContext(mdx));

        foreach (var word in words)
        {
            var result = new DictionaryResult();
            await plugin.TranslateAsync(word, result, CancellationToken.None);
            var card = BuildCard(result);
            var path = Path.Combine(outDir, $"preview-{word.Replace(' ', '_')}.png");
            Render(card, path);
            Console.WriteLine($"{word}: {result.ResultType} → {path}");
        }
        plugin.Dispose();
    }

    private static FrameworkElement BuildCard(DictionaryResult r)
    {
        var body = new StackPanel { Margin = new Thickness(8, 8, 8, 4) };

        // 单词
        body.Children.Add(new TextBlock { Text = r.Text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });

        // 音标：标签徽章 + /音标/ + 喇叭
        if (r.Symbols.Count > 0)
        {
            // 宿主用的是不换行的横排（SimpleStackPanel），放不下会被裁掉
            var symbols = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4), ClipToBounds = true };
            foreach (var s in r.Symbols)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 0) };
                var badge = new Border { Margin = new Thickness(0, 3, 5, 3), Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(2) };
                badge.SetResourceReference(Border.BackgroundProperty, ThemeKeys.AccentAAFillColorDefaultBrushKey);
                var label = new TextBlock { Text = s.Label, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextOnAccentFillColorPrimaryBrushKey);
                badge.Child = label;
                item.Children.Add(badge);
                item.Children.Add(new TextBlock { Text = $"/{s.Phonetic}/", FontFamily = new FontFamily("Consolas, Courier New"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                if (!string.IsNullOrEmpty(s.AudioUrl))
                    item.Children.Add(new TextBlock { Text = "🔊", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                symbols.Children.Add(item);
            }
            body.Children.Add(symbols);
        }

        // 词性 + 释义（宿主用“；”连接 Means）
        var means = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var m in r.DictMeans)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (!string.IsNullOrEmpty(m.PartOfSpeech))
            {
                var pos = new TextBlock { Text = m.PartOfSpeech, FontSize = 12, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 3, 0, 0) };
                pos.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.AccentTextFillColorPrimaryBrushKey);
                grid.Children.Add(pos);
            }
            var text = new TextBlock { Text = string.Join("；", m.Means), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 0) };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            means.Children.Add(grid);
        }
        body.Children.Add(means);

        // 词形变化、标签
        AddExchange(body, "复数", r.Plurals);
        AddExchange(body, "过去式", r.PastTense);
        AddExchange(body, "过去分词", r.PastParticiple);
        AddExchange(body, "现在分词", r.PresentParticiple);
        AddExchange(body, "第三人称单数", r.ThirdPersonSingular);
        AddExchange(body, "比较级", r.Comparative);
        AddExchange(body, "最高级", r.Superlative);
        AddExchange(body, "标签", r.Tags);

        // 例句
        var sentences = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var s in r.Sentences)
        {
            var border = new Border { Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(4) };
            border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.CardBackgroundFillColorSecondaryBrushKey);
            border.Child = new TextBlock { Text = s, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap };
            sentences.Children.Add(border);
        }
        body.Children.Add(sentences);

        // 外框：服务名标题 + 内容卡片
        var header = new TextBlock { Text = "本地词典", FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 8, 12, 4) };
        var card = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(8), Child = new StackPanel { Children = { header, body } } };
        card.SetResourceReference(Border.BackgroundProperty, ThemeKeys.CardBackgroundFillColorDefaultBrushKey);
        card.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.CardStrokeColorDefaultBrushKey);
        var root = new Border { Width = WindowWidth, Child = card };
        root.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SolidBackgroundFillColorBaseBrushKey);
        TextElement.SetFontFamily(root, new FontFamily("Microsoft YaHei UI"));
        TextElement.SetFontSize(root, 14);
        return root;
    }

    private static void AddExchange(Panel parent, string title, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock { Text = title + "：", Opacity = 0.7, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var item in list)
        {
            var link = new TextBlock { Text = item, Margin = new Thickness(0, 0, 10, 0) };
            link.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.AccentTextFillColorPrimaryBrushKey);
            row.Children.Add(link);
        }
        parent.Children.Add(row);
    }

    private static void Render(FrameworkElement element, string path)
    {
        var window = new Window { Content = element, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -4000, Top = 0 };
        window.Show();
        window.UpdateLayout();
        var dpi = 1.5;
        var bmp = new RenderTargetBitmap((int)(element.ActualWidth * dpi), (int)(element.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var png = File.Create(path))
            encoder.Save(png);
        window.Close();
    }
}
