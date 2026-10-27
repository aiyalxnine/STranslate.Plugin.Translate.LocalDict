<#
.SYNOPSIS
    编译插件并打包为可直接导入 STranslate 的 .spkg。

.PARAMETER STranslateHome
    STranslate 安装目录下的 current 文件夹（内含 STranslate.Plugin.dll）。
    默认 %LOCALAPPDATA%\STranslate\current。

.PARAMETER Dotnet
    dotnet 可执行文件，默认使用 PATH 中的 dotnet（需要 .NET 10 SDK）。

.EXAMPLE
    ./build.ps1
    ./build.ps1 -STranslateHome "D:\Apps\STranslate\current"
#>
param(
    [string]$STranslateHome = "$env:LOCALAPPDATA\STranslate\current",
    [string]$Dotnet = "dotnet"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\STranslate.Plugin.Translate.LocalDict\STranslate.Plugin.Translate.LocalDict.csproj'
$out = Join-Path $root 'artifacts\out'

if (-not (Test-Path (Join-Path $STranslateHome 'STranslate.Plugin.dll'))) {
    throw "找不到 $STranslateHome\STranslate.Plugin.dll，请用 -STranslateHome 指定 STranslate 的 current 目录"
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $Dotnet build $project -c Release -o $out "-p:STranslateHome=$STranslateHome"
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

$version = (Get-Content (Join-Path $out 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Version
$spkg = Join-Path $root "artifacts\STranslate.Plugin.Translate.LocalDict-$version.spkg"
if (Test-Path $spkg) { Remove-Item $spkg -Force }

# .spkg 就是 zip，plugin.json 必须位于压缩包根目录
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($out, $spkg, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "已生成 $spkg"
