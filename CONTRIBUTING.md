# 参与贡献

欢迎提交 Issue 和 Pull Request。

## 本地构建与测试

需要 .NET 10 SDK 和本机安装的 STranslate（编译时引用它的 `STranslate.Plugin.dll`）。

```powershell
./build.ps1                                            # 编译并打包 .spkg 到 artifacts/
dotnet run --project tools/Probe -c Release -- selftest  # 自带合成词库的自动测试
```

CI 会在每个 PR 上执行相同步骤（GitHub 托管的 Windows 机器，`STranslate.Plugin.dll` 取自 STranslate 官方 v2.0.10 发布包并校验哈希）。

## 提交 PR 的要求

- CI 通过。
- 修复缺陷或新增解析规则时，在 `tools/Probe/SelfTest.cs` 里补测试。测试词条请**自拟**，只模仿词库的标记结构。
- **不要提交任何真实词典内容**（词条 HTML、截图、MDX/MDD 文件）。
- 插件保持离线：不新增网络请求，本地发音服务只监听 127.0.0.1。
- 不提交编译产物或其他二进制文件。
- 一个 PR 只做一件事，并说明动机。如果改变了显示效果，请描述改动前后的差别。

## 审核与合并

日常审核由仓库所有者委托的 AI 助手（Claude）代为进行：逐行阅读改动，并确认 CI 结果。

下列改动需要仓库所有者本人确认后才会合并：

- 构建与发布相关：`.github/`、`build.ps1`、`*.csproj` / `*.props` / `*.targets`、NuGet 依赖。
- 涉及网络访问、启动进程、注册表、插件设置以外的文件写入、动态加载程序集、混淆代码或二进制内容的改动。
- 许可证或 `plugin.json` 中插件 ID、作者的变更。

合并方式为 squash merge。
