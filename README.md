# STranslate 本地词典插件（LocalDict）

[STranslate](https://github.com/STranslate/STranslate) 的离线词典插件：查询本机的 **MDX 词库**，用 STranslate 原生的词典界面分栏显示音标、释义、词形变化和例句，并可播放同名 **MDD** 中的真人发音。全部在本机完成，不联网。

> An offline dictionary plugin for STranslate 2.x. It looks words up in local MDict (`.mdx`) dictionaries, shows phonetics, senses, word forms and examples in STranslate's native dictionary view, and plays pronunciation audio from the matching `.mdd` file.

## 功能

- **音标与真人发音**：显示英、美音标，点喇叭播放 `.mdd` 里的 MP3。不同词性读音不同的词（present *adj.* /ˈprezənt/、*v.* /prɪˈzent/）会分别列出并标注词性。
- **结构化释义**：每个义项一行，左侧是“词性+义项号”（如 `v.1a`），右侧依次是路标〔〕、语法码 [C]/[U]/[I]/[T]、语体和地域〈非正式〉〈英式〉、中文释义，后面跟英文释义（可关闭）。
- **词形变化**：复数、过去式、过去分词、现在分词、第三人称单数、比较级、最高级，点击可接着查。
- **标签与例句**：词频、语法、语体等标签；中英对照例句优先每个义项各取一句，数量可设置。
- **查不到原词时**：
  - 还原词形（running → run，happier → happy，studies → study）。
  - went、children 这类“某词的过去式/复数”词条，会补上原形的完整词条。
  - 短语动词（look up、ran across）到动词原形的词条里查找。
- **多词库**：可添加多本 MDX，按顺序查询，每本单独启用或调整顺序。

### 词库支持

- 格式：MDict 1.2 / 2.0；数据块压缩支持 zlib、LZO 和不压缩；支持只加密索引的 `Encrypted=2`。
- 编码：UTF-8、UTF-16、GBK/GB18030、Big5。
- **朗文（LDOCE5++/6++ 等同类结构）** 按结构化方式解析：
  - 美音里只写变化部分的写法（`$ -ˈloʊ`、`$ -ˈɑː-`）会补全为完整音标。
  - 例句、语体、地域标签都会单独提取。
- **其他 MDX 词库** 转成按行排列的文字释义，并尽量识别音标和第一个发音。
- 不支持 MDict 3.0，也不支持需要注册码的加密词库（`Encrypted=1`）。

本项目**不包含任何词典数据**，请使用你有权使用的词库。

## 安装

要求：STranslate 2.x（Windows）。本插件在 STranslate 2.0.10 上开发和测试。

1. 在 [Releases](https://github.com/aiyalxnine/STranslate.Plugin.Translate.LocalDict/releases) 下载 `STranslate.Plugin.Translate.LocalDict-x.y.z.spkg`。
2. 打开 STranslate “设置 → 插件”，把 `.spkg` 拖进插件页面，或使用本地导入。
3. 在“设置 → 服务 → 文本翻译”里点“添加”，选择“本地词典”。
4. 在该服务的设置里点“添加 MDX 词库”，选择 `.mdx` 文件。同一文件夹里的同名 `.mdd`（以及分卷 `.1.mdd`、`.2.mdd` …）会自动作为发音库。
5. 划词或输入单词即可查询。

插件只读取词库文件，不会复制或修改它们。移除词库只是从列表中去掉，文件保持不动。

## 设置项

| 设置 | 默认 | 说明 |
|---|---|---|
| 词库列表 | 空 | 按列表顺序查询，可单独启用、上移/下移、移除 |
| 显示英文释义 | 启用 | 英汉双解词库在中文释义后附上英文释义 |
| 显示短语动词列表 | 启用 | 在动词词条下列出短语动词 |
| 例句数量 | 6 | 0 / 3 / 6 / 10 / 20 |

## 实现说明

- **发音**：STranslate 的发音按钮只会通过 HTTP 下载 `Symbol.AudioUrl`。插件在本机启动了一个只监听 `127.0.0.1` 的小型服务，把 `.mdd` 里的音频交给它。
  - 地址带随机令牌，外部网络访问不到。
  - 端口和令牌保存在插件设置里，所以 STranslate 历史记录中的发音地址在重启后仍然有效。
  - 原端口被占用时会自动换一个。
- **音标行宽度**：STranslate 的音标行不会换行。插件会估算宽度，先放各词性的英音，放得下再放美音，避免被挤出窗口。
- **索引**：STranslate 启动时在后台建立索引。以 26 万词条、18 万发音资源的朗文词库为例，约 0.3 秒、常驻内存约 64 MB；单次查词通常只需几毫秒到几十毫秒。
- **HTML 解析**：这类词库的 HTML 常有未闭合的标签，解析后的层级和浏览器里看到的不一致。朗文解析器因此按文档顺序线性扫描（遇到词头就开始新词条），不依赖嵌套关系。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，以及本机已安装的 STranslate（编译时引用它的 `STranslate.Plugin.dll`）。

```powershell
./build.ps1                                                # 默认读取 %LOCALAPPDATA%\STranslate\current
./build.ps1 -STranslateHome "D:\Apps\STranslate\current"   # STranslate 装在别处时
```

输出：`artifacts/STranslate.Plugin.Translate.LocalDict-<版本>.spkg`。

```
src/STranslate.Plugin.Translate.LocalDict/
  Mdict/        MDX/MDD 读取（文件格式、RIPEMD-128 索引解密、LZO/zlib 解压）
  Core/         查询、朗文解析、通用解析、词形还原、标签处理、本地发音服务
  Main.cs       映射到 STranslate 的 DictionaryResult
  View/ ViewModel/ Languages/   设置界面与中英文文案
tools/
  Probe/        命令行探查与回归测试（不依赖 STranslate）
  HostSmoke/    按 STranslate 的方式加载插件：查词、取发音、渲染设置界面和结果预览
```

自动测试（自带合成词库，不需要真实词典，CI 也运行它）：

```powershell
dotnet run --project tools/Probe -c Release -- selftest
```

用真实词库排查问题：

```powershell
# 结构化解析结果
dotnet run --project tools/Probe -c Release -- parse <词库.mdx> nausea "look up" went
# 随机抽查 N 个词条，统计失败和空结果
dotnet run --project tools/Probe -c Release -- bulk <词库.mdx> 1500
# 模拟宿主加载插件并截图设置界面（插件目录为 artifacts/out）
dotnet run --project tools/HostSmoke -c Release -- artifacts/out <词库.mdx> settings.png
# 按宿主模板复刻渲染查询结果
dotnet run --project tools/HostSmoke -c Release -- preview artifacts/out <词库.mdx> previews record hello
```

## 参与贡献

欢迎 Issue 和 PR，请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 致谢

- [STranslate](https://github.com/STranslate/STranslate)：插件接口与宿主。
- MDict 文件格式参考了社区的公开分析，例如 [writemdict 的格式说明](https://github.com/zhansliu/writemdict/blob/master/fileformat.md)。
- [HtmlAgilityPack](https://html-agility-pack.net/)：HTML 解析。

## 许可证

[MIT](LICENSE)。第三方组件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

本项目不是 STranslate 官方插件。
