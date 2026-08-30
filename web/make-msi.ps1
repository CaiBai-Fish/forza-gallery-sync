# make-msi.ps1 - Build the Forza Gallery Sync MSI installer (per-user, x64)
#
# Pipeline:
#   1. Publish the WinUI 3 app as a self-contained folder (app + .NET runtime
#      + Windows App SDK runtime) -> staging\app
#   2. Extract the Python runtime into staging\app\python (install-dir env)
#   3. Build the DTF managed custom action (db-preserve on uninstall)
#   4. Generate app.wxs (component manifest) from staging\app via PowerShell
#      (deterministic MD5-based component GUIDs; stable across builds)
#   5. Build the MSI with WiX v4: installer\msi\Product.wxs + app.wxs
#      -> web\dist\ForzaGallerySync-<version>.msi
#
# Requires: WiX v4 as a local dotnet tool (dotnet tool restore at repo root)
#
# Usage (in web dir):
#   powershell -ExecutionPolicy Bypass -File .\make-msi.ps1 [-PythonEnv <dir>] [-Version <x.y.z>]
# Output: web\dist\ForzaGallerySync-<version>.msi
param(
    [string]$Config = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.4.2",
    [string]$PythonEnv = ""
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path        # web/
$projectRoot = Split-Path -Parent $root                        # repo root
$appCsproj = Join-Path $root "ForzaGallerySync.csproj"
$msiDir = Join-Path $projectRoot "installer\msi"
$caDir = Join-Path $msiDir "customaction"
$caCsproj = Join-Path $caDir "ForzaGallerySync.CA.csproj"
$distDir = Join-Path $root "dist"
$stageDir = Join-Path $root "obj\msi-stage"
$appDir = Join-Path $stageDir "app"
$appWxs = Join-Path $stageDir "app.wxs"
$finalMsi = Join-Path $distDir "ForzaGallerySync-$Version.msi"

if (-not (Test-Path $appCsproj)) { throw "App project not found: $appCsproj" }
if (-not (Test-Path $caCsproj)) { throw "CA project not found: $caCsproj" }
if (-not (Test-Path (Join-Path $msiDir "Product.wxs"))) { throw "Product.wxs not found in $msiDir" }

# 未显式指定 Python 环境时，从 PATH 自动探测（与 make-installer.ps1 一致）
if (-not $PythonEnv) {
    $pyCmd = Get-Command python -ErrorAction SilentlyContinue
    if ($pyCmd) {
        $PythonEnv = Split-Path $pyCmd.Source -Parent
        Write-Host "==> Auto-detected PythonEnv: $PythonEnv (from PATH)"
    } else {
        throw "Python environment not found: pass -PythonEnv <dir> or ensure python is on PATH."
    }
}

# ---- clean stage (transient file locks: Defender/AV scanning; retry) ----
function Remove-Stage([string]$dir) {
    for ($i = 0; $i -lt 8; $i++) {
        try {
            if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop }
            return
        } catch {
            Start-Sleep -Milliseconds 1500
        }
    }
    # give up silently: leftover files are overwritten anyway
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Stage $stageDir
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# ---- 1. publish app (self-contained folder: exe + .NET runtime + WinAppSDK) ----
Write-Host "==> [1/5] Publishing app (self-contained folder) ..."
dotnet publish $appCsproj `
    -c $Config -r $Runtime -p:Platform=x64 `
    --self-contained true -o $appDir
if ($LASTEXITCODE -ne 0) { throw "App publish failed (exit $LASTEXITCODE)" }

# ---- 2. python runtime -> app\python (used from install dir) ----
Write-Host "==> [2/5] Extracting Python runtime -> app\python ..."
& (Join-Path $root "make-runtime.ps1") -ExtractTo (Join-Path $appDir "python") -PythonEnv $PythonEnv
if ($LASTEXITCODE -ne 0) { throw "make-runtime failed (exit $LASTEXITCODE)" }

# ---- 3. build DTF custom action (x64) ----
Write-Host "==> [3/5] Building custom action (DTF, x64) ..."
dotnet build $caCsproj -c $Config
if ($LASTEXITCODE -ne 0) { throw "Custom action build failed (exit $LASTEXITCODE)" }
$caDll = Get-ChildItem $caDir -Recurse -Filter "ForzaGallerySync.CA.CA.dll" |
    Where-Object { $_.FullName -match "x64" } | Select-Object -First 1
if (-not $caDll) { throw "Custom action DLL (ForzaGallerySync.CA.CA.dll) not found under $caDir" }
$caBinary = $caDll.FullName
$iconFile = Join-Path $projectRoot "installer\forza-gallery-sync.ico"
Write-Host "    CA DLL: $caBinary"

# ---- 4. generate app.wxs (component manifest) ----
Write-Host "==> [4/5] Generating component manifest app.wxs ..."
. (Join-Path $PSScriptRoot "msi-generate.ps1") -AppDir $appDir -OutWxs $appWxs
if ($LASTEXITCODE -ne 0) { throw "app.wxs generation failed (exit $LASTEXITCODE)" }

# ---- 5. build MSI with WiX v4 ----
Write-Host "==> [5/5] Building MSI ..."
Push-Location $projectRoot
try {
    dotnet wix build $msiDir\Product.wxs $appWxs `
        -o $finalMsi `
        -arch x64 `
        -d ProductVersion=$Version `
        -d CaBinary=$caBinary `
        -d IconFile=$iconFile
    if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$mb = [math]::Round((Get-Item $finalMsi).Length / 1MB, 1)
Write-Host "==> Done: $finalMsi ($mb MB)"
exit 0
