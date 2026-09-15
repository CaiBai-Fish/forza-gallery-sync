# ============================================================
#  Forza Gallery Sync - 生成标准 EXE 安装程序（Inno Setup 6）
#
#  为什么用 Inno Setup：Windows 上最常用的 exe 安装程序方案，免费、脚本化友好，
#  且支持 per-user 安装（免管理员）—— 与项目的自动更新机制配套：
#    程序目录必须对当前用户可写，否则"下载新版本覆盖程序目录"只能退化成手动更新。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File .\web\make-installer.ps1 `
#      -SourceDir web\dist\ForzaGallerySync-1.0.4-win-x64 -Version 1.0.4
#  产物：<OutputDir>\ForzaGallerySync-<版本>-setup.exe（默认 OutputDir = web\dist）
#
#  ISCC.exe 定位顺序：PATH → 常见的 Inno Setup 6/5 安装目录；
#  都没有时给出明确提示（可 winget install JRSoftware.InnoSetup）。
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDir = ""
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path        # web/
$scriptPath = Join-Path $root "Resources\installer.iss"
if (-not (Test-Path -LiteralPath $scriptPath)) { throw "找不到安装脚本: $scriptPath" }

$src = (Resolve-Path -LiteralPath $SourceDir).Path
if (-not $OutputDir) { $OutputDir = Join-Path $root "dist" }
if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$out = (Resolve-Path -LiteralPath $OutputDir).Path

# 发布目录必须完整：安装器打包的是整目录，少了关键文件会成为坏安装包
foreach ($required in @('forza-gallery-sync.exe', 'forza-gallery-sync.dll', 'resources.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $src $required))) {
        throw "发布目录不完整，缺少 $required：$src（先跑 make-gui.ps1）"
    }
}

# 定位 ISCC.exe
$iscc = $null
$cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($cmd) { $iscc = $cmd.Source }
if (-not $iscc) {
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 5\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path -LiteralPath $p) { $iscc = $p; break }
    }
}
if (-not $iscc) {
    throw "找不到 ISCC.exe（Inno Setup 编译器）。安装方式：winget install JRSoftware.InnoSetup"
}

Write-Host "==> 安装程序编译器: $iscc"
Write-Host "==> 源目录: $src"
Write-Host "==> 版本: $Version"

# 可选项：简体中文语言文件（仓库自带）与安装程序图标（取发布目录里的应用图标）。
# 两者都是"有就用、没有就退回"，避免因缺文件让整个安装包构建失败。
$isccArgs = @("/DAppVersion=$Version", "/DSourceDir=$src", "/DOutputDir=$out")

$chineseIsl = Join-Path $root "Resources\Languages\ChineseSimplified.isl"
if (Test-Path -LiteralPath $chineseIsl) {
    $isccArgs += "/DHasChinese=1"
    Write-Host "==> 语言: 简体中文 + English"
} else {
    Write-Host "==> 语言: 仅 English（未找到 $chineseIsl）"
}

$icon = Join-Path $src "Assets\forza-gallery-sync.ico"
if (Test-Path -LiteralPath $icon) {
    $isccArgs += "/DIcoFile=$icon"
    Write-Host "==> 图标: $icon"
} else {
    Write-Host "==> 图标: 使用 Inno 默认图标（发布目录没有 Assets\forza-gallery-sync.ico）"
}

$steps = (Get-ChildItem -LiteralPath $src -Recurse -File)
Write-Host ("==> 打包内容: {0} 个文件 / {1:N1} MB（LZMA2 压缩，需要几分钟）" -f `
    $steps.Count, (($steps | Measure-Object Length -Sum).Sum / 1MB))

$LASTEXITCODE = 0
& $iscc @isccArgs $scriptPath
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（exit $LASTEXITCODE）" }

$setup = Join-Path $out "ForzaGallerySync-$Version-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "编译结束但没找到产物: $setup" }

$info = (Get-Item -LiteralPath $setup).VersionInfo
Write-Host ""
Write-Host ("==> Done: {0}" -f $setup)
Write-Host ("    大小 {0:N1} MB；FileVersion={1}" -f ((Get-Item $setup).Length / 1MB), $info.FileVersion)
exit 0
