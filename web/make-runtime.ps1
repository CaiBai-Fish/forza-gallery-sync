# Build the Python runtime package from the FGS conda env (including the
# forza_sync package).
#   - Always produces web\python-runtime.zip, embedded into the app assembly
#     (App\Services\PythonHost.cs). The GUI program extracts it on first run,
#     either into its own program directory (clean/portable directory) or into
#     the default install directory (%LOCALAPPDATA%\Programs\ForzaGallerySync).
#   - Optionally, with -ExtractTo <dir>, also writes the extracted runtime
#     directory (handy for local debugging).
#
# Usage (in web dir):
#   powershell -ExecutionPolicy Bypass -File .\make-runtime.ps1 [-ExtractTo <dir>]
# Output: web\python-runtime.zip
param(
    [string]$PythonEnv = "",
    [string]$ExtractTo = ""
)
$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot
$zipPath = Join-Path $projectDir "python-runtime.zip"
$stageDir = Join-Path $env:TEMP "fgs-runtime-stage"

# 未显式指定 Python 环境时，从 PATH 自动探测（避免硬编码本机绝对路径）
if (-not $PythonEnv) {
    $pyCmd = Get-Command python -ErrorAction SilentlyContinue
    if ($pyCmd) {
        $PythonEnv = Split-Path $pyCmd.Source -Parent
        Write-Host "==> Auto-detected PythonEnv: $PythonEnv (from PATH)"
    } else {
        throw "Python environment not found: pass -PythonEnv <dir> or ensure python is on PATH."
    }
}
if (-not (Test-Path (Join-Path $PythonEnv "python313.dll"))) {
    throw "Invalid Python home: $PythonEnv (python313.dll not found)"
}

# ---- Preflight: the source environment must carry every runtime dependency ----
# Why this is a hard failure and not a warning: without playwright the packaged app
# silently loses "browser login" (the feature just errors at runtime), and the only
# sign used to be a warning at the end of this script -- which was easy to skim past
# (it shipped a runtime without playwright twice). Fail here with the exact fix instead.
Write-Host "==> Verifying source environment"
$required = @{
    "requests"   = "core HTTP client"
    "urllib3"    = "requests dependency chain"
    "certifi"    = "requests dependency chain"
    "idna"       = "requests dependency chain"
    "charset_normalizer" = "requests dependency chain"
    "playwright" = "browser login (one-click sign-in)"
    "greenlet"   = "playwright sync API"
    "pyee"       = "playwright event emitter"
}
$pyExe = Join-Path $PythonEnv "python.exe"
$missing = @()
foreach ($name in $required.Keys) {
    $probe = "import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('$name') else 1)"
    & $pyExe -c $probe 2>$null
    if ($LASTEXITCODE -ne 0) {
        $missing += $name
        Write-Host ("    MISSING: {0} ({1})" -f $name, $required[$name])
    }
}
if ($missing.Count -gt 0) {
    throw ("源环境缺少依赖：{0}`n" +
           "请在打包用环境里安装后重试： & '{1}' -m pip install -r requirements.txt" -f
           ($missing -join ", "), $pyExe)
}
Write-Host "    OK: $($required.Count) 个依赖齐备（含 playwright）"

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Write-Host "==> Copy core DLLs"
Copy-Item "$PythonEnv/python313.dll" $stageDir -Force
Copy-Item "$PythonEnv/python3.dll" $stageDir -Force
Copy-Item "$PythonEnv/vcruntime140.dll" $stageDir -Force -ErrorAction SilentlyContinue
Copy-Item "$PythonEnv/vcruntime140_1.dll" $stageDir -Force -ErrorAction SilentlyContinue
Copy-Item "$PythonEnv/msvcp140.dll" $stageDir -Force -ErrorAction SilentlyContinue
Copy-Item "$PythonEnv/zlib.dll" $stageDir -Force -ErrorAction SilentlyContinue

Write-Host "==> Copy Lib (stdlib + site-packages) ..."
Copy-Item "$PythonEnv/Lib" "$stageDir/Lib" -Recurse -Force

# Prune site-packages: keep only what the app needs
# requests chain: requests / urllib3 / charset_normalizer / idna / certifi
# browser login: playwright (+ greenlet for sync API, pyee for the event emitter,
#                typing_extensions kept as a site-packages file below)
Write-Host "==> Prune site-packages"
$keep = @(
    "requests", "urllib3", "charset_normalizer", "idna", "certifi",
    "playwright", "greenlet", "pyee",
    "pip", "setuptools", "_distutils_hack", "distutils-precedence"
)
Get-ChildItem "$stageDir/Lib/site-packages" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $name = $_.Name
    $keepIt = $false
    foreach ($k in $keep) {
        if ($name -eq $k -or $name -like "$k-*") { $keepIt = $true; break }
    }
    if (-not $keepIt) { Remove-Item $_.FullName -Recurse -Force }
}
# 单文件模块：*.pth（路径注入）、typing_extensions.py（playwright/pyee 兜底依赖）保留
$keepFiles = @("typing_extensions.py")
Get-ChildItem "$stageDir/Lib/site-packages" -File -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Name -notlike "*.pth" -and $_.Name -notlike "README*" -and $keepFiles -notcontains $_.Name) {
        Remove-Item $_.FullName -Force
    }
}

Write-Host "==> Copy DLLs (.pyd extension modules)"
Copy-Item "$PythonEnv/DLLs" "$stageDir/DLLs" -Recurse -Force

if (Test-Path "$PythonEnv/Library/bin") {
    Write-Host "==> Copy Library/bin (sqlite3 etc.)"
    New-Item -ItemType Directory -Force -Path "$stageDir/Library" | Out-Null
    Copy-Item "$PythonEnv/Library/bin" "$stageDir/Library/bin" -Recurse -Force
} else {
    Write-Host "==> [warn] Library/bin not found (standard CPython); using DLLs/ extension modules"
}

Write-Host "==> Copy forza_sync package"
Copy-Item (Join-Path $projectDir "..\forza_sync") "$stageDir/forza_sync" -Recurse -Force

# Clean caches
Get-ChildItem $stageDir -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $stageDir -Recurse -File -Filter "*.pyc" | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "==> Compress ..."
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$stageDir/*" -DestinationPath $zipPath -Force

# ---- sanity check: 归档必须带上 forza_sync、requests 与 playwright ----
# 注意：zip 条目用的是**反斜杠**分隔符（`Compress-Archive` 的行为），
# 所以匹配要写成 `[\\/]`；只写 `/` 会全部匹配不到、把"其实有"误判成"缺失"（实测踩过）。
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
$entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
$archive.Dispose()

function Assert-Bundled([string[]]$names, [string]$pattern, [string]$label) {
    if (-not ($names -match $pattern)) {
        throw "Runtime zip 缺少 $label（$zipPath）。请在打包用环境里安装后重新打包。"
    }
    Write-Host "    OK: $label"
}

Write-Host "==> Verify archive contents ($($entryNames.Count) entries)"
Assert-Bundled $entryNames '^forza_sync[\\/]' 'forza_sync 包'
Assert-Bundled $entryNames 'site-packages[\\/]requests[\\/]' 'requests'
Assert-Bundled $entryNames 'site-packages[\\/]playwright[\\/]' 'playwright'
Assert-Bundled $entryNames 'site-packages[\\/]playwright[\\/]driver[\\/]node\.exe$' 'playwright driver (node.exe)'
Write-Host "==> 浏览器登录可用（playwright 已随运行时打包）"

# Optional: also emit the extracted runtime directory (handy for local debugging)
if ($ExtractTo) {
    Write-Host "==> Extract folder -> $ExtractTo"
    if (Test-Path $ExtractTo) { Remove-Item $ExtractTo -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ExtractTo | Out-Null
    Copy-Item "$stageDir/*" $ExtractTo -Recurse -Force
}

$mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "==> Done: $zipPath ($mb MB)"

Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
# 显式退出码：脚本可能被 make-gui.ps1 以 & 进程内调用，
# 正常结束不会自动更新 $LASTEXITCODE，需显式置 0 供调用方判断。
exit 0
