# Build the Python runtime package from the FGS conda env (including the
# forza_sync package).
#   - Always produces web\python-runtime.zip (embedded as an assembly resource
#     for dev/portable fallback).
#   - Optionally, with -ExtractTo <dir>, also writes the extracted runtime
#     directory (used by make-installer.ps1 to place python\ next to the app
#     in the install directory).
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
Write-Host "==> Prune site-packages"
$keep = @("requests", "urllib3", "charset_normalizer", "idna", "certifi", "pip", "setuptools", "_distutils_hack", "distutils-precedence")
Get-ChildItem "$stageDir/Lib/site-packages" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $name = $_.Name
    $keepIt = $false
    foreach ($k in $keep) {
        if ($name -eq $k -or $name -like "$k-*") { $keepIt = $true; break }
    }
    if (-not $keepIt) { Remove-Item $_.FullName -Recurse -Force }
}
Get-ChildItem "$stageDir/Lib/site-packages" -File -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Name -notlike "*.pth" -and $_.Name -notlike "README*") { Remove-Item $_.FullName -Force }
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

# Optional: also emit the extracted runtime directory (used by make-installer.ps1)
if ($ExtractTo) {
    Write-Host "==> Extract folder -> $ExtractTo"
    if (Test-Path $ExtractTo) { Remove-Item $ExtractTo -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ExtractTo | Out-Null
    Copy-Item "$stageDir/*" $ExtractTo -Recurse -Force
}

$mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "==> Done: $zipPath ($mb MB)"

Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
# 显式退出码：脚本可能被 make-installer.ps1 以 & 进程内调用，
# 正常结束不会自动更新 $LASTEXITCODE，需显式置 0 供调用方判断。
exit 0
