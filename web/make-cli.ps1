# make-cli.ps1 - Build the Forza Gallery Sync CLI (backend) with Nuitka
# Usage: powershell -ExecutionPolicy Bypass -File make-cli.ps1 [-Python <python.exe>] [-Version <ver>]
# Produces a standalone single-file exe (forza-sync.exe) with embedded Python.
param(
    [string]$Python = "",
    [string]$Version = "0.4.0"
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path        # web/
$projectRoot = Split-Path -Parent $root                        # repo root
$outDir = Join-Path $projectRoot "cli-dist"
$entry = Join-Path $projectRoot "cli_entry.py"

if (-not (Test-Path $entry)) { throw "Entry script not found: $entry" }

# 未显式指定 Python 时，从 PATH 自动探测（避免硬编码本机绝对路径）
if (-not $Python) {
    $pyCmd = Get-Command python -ErrorAction SilentlyContinue
    if ($pyCmd) {
        $Python = $pyCmd.Source
        Write-Host "    Auto-detected Python: $Python"
    } else {
        throw "Python not found: pass -Python <python.exe> or ensure python is on PATH."
    }
}
if (-not (Test-Path $Python)) { throw "Python not found: $Python" }

$pythonHome = Split-Path $Python -Parent

# 定位 sqlite3.dll（conda: Library/bin；标准 CPython: DLLs），供 sqlite3 扩展使用
$sqliteDll = @(
    (Join-Path $pythonHome "Library/bin/sqlite3.dll"),
    (Join-Path $pythonHome "DLLs/sqlite3.dll"),
    (Join-Path $pythonHome "sqlite3.dll")
) | Where-Object { Test-Path $_ } | Select-Object -First 1

Write-Host "==> Building Forza Gallery Sync CLI (onefile) with Nuitka..."
Write-Host "    Python: $Python"
Write-Host "    Entry : $entry"
Write-Host "    Output: $outDir"
if ($sqliteDll) { Write-Host "    sqlite3.dll: $sqliteDll" } else { Write-Host "    sqlite3.dll: not found (skipping explicit include)" }

$nuitkaArgs = @(
    "-m", "nuitka",
    "--onefile",
    "--include-package=forza_sync",
    "--assume-yes-for-downloads",
    "--output-dir=$outDir",
    "--product-name=Forza Gallery Sync CLI",
    "--file-description=Forza Gallery Sync command line tool (sync, config, login, token, status)",
    "--file-version=$Version",
    "--enable-plugin=no-qt"
)
if ($sqliteDll) {
    $nuitkaArgs += "--include-data-files=$sqliteDll=./sqlite3.dll"
}
$nuitkaArgs += $entry

& $Python @nuitkaArgs
if ($LASTEXITCODE -ne 0) { throw "Nuitka build failed (exit $LASTEXITCODE)" }

$built = Join-Path $outDir "cli_entry.exe"
$final = Join-Path $outDir "forza-sync.exe"
if (Test-Path $built) {
    if (Test-Path $final) { Remove-Item $final -Force }
    Rename-Item $built $final
    Write-Host "==> Done: $final"
    Write-Host "    Size: $([math]::Round((Get-Item $final).Length / 1MB, 1)) MB"
} else {
    throw "Expected output not found: $built"
}
