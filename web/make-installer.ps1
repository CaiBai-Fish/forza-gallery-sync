# make-installer.ps1 - Build the Forza Gallery Sync GUI installer
#
# Pipeline:
#   1. Publish the WinUI 3 app as a self-contained folder (app + .NET runtime
#      + Windows App SDK runtime) -> staging\app
#   2. Extract the Python runtime into staging\app\python (install-dir env)
#   3. Compress staging\app into payload.zip and copy it into the installer
#      project (installer\payload.zip, embedded as a resource)
#   4. Publish the installer exe (single-file, self-contained, payload embedded)
#
# The installer extracts everything into the install directory at setup time;
# the app then uses the Python/.NET runtime directly from the install dir.
#
# Usage (in web dir):
#   powershell -ExecutionPolicy Bypass -File .\make-installer.ps1
# Output: web\dist\ForzaGallerySync-Setup-<version>.exe
param(
    [string]$Config = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.4.1",
    [string]$PythonEnv = ""
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path        # web/
$projectRoot = Split-Path -Parent $root                        # repo root
$appCsproj = Join-Path $root "ForzaGallerySync.csproj"
$setupDir = Join-Path $projectRoot "installer"
$setupCsproj = Join-Path $setupDir "ForzaGallerySync.Setup.csproj"
$distDir = Join-Path $root "dist"
$stageDir = Join-Path $root "obj\installer-stage"
$appDir = Join-Path $stageDir "app"
$payloadZip = Join-Path $stageDir "payload.zip"
$setupOut = Join-Path $stageDir "setup-out"
$finalExe = Join-Path $distDir "ForzaGallerySync-Setup-$Version.exe"

if (-not (Test-Path $appCsproj)) { throw "App project not found: $appCsproj" }
if (-not (Test-Path $setupCsproj)) { throw "Setup project not found: $setupCsproj" }

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

# 干净检出后没有 python-runtime.zip 时先生成（供应用内嵌回退）
$runtimeZip = Join-Path $root "python-runtime.zip"
if (-not (Test-Path $runtimeZip)) {
    Write-Host "==> [pre] Generating python-runtime.zip (embedded fallback) ..."
    & (Join-Path $root "make-runtime.ps1") -PythonEnv $PythonEnv
    if ($LASTEXITCODE -ne 0) { throw "make-runtime (pre) failed (exit $LASTEXITCODE)" }
}

# ---- clean stage ----
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# ---- 1. publish app (self-contained folder: exe + .NET runtime + WinAppSDK) ----
Write-Host "==> [1/4] Publishing app (self-contained folder) ..."
dotnet publish $appCsproj `
    -c $Config -r $Runtime -p:Platform=x64 `
    --self-contained true -o $appDir
if ($LASTEXITCODE -ne 0) { throw "App publish failed (exit $LASTEXITCODE)" }

# ---- 2. python runtime -> app\python (extracted, used from install dir) ----
Write-Host "==> [2/4] Extracting Python runtime -> app\python ..."
& (Join-Path $root "make-runtime.ps1") -ExtractTo (Join-Path $appDir "python") -PythonEnv $PythonEnv
if ($LASTEXITCODE -ne 0) { throw "make-runtime failed (exit $LASTEXITCODE)" }

# ---- 3. bundle payload.zip into the installer project ----
Write-Host "==> [3/4] Bundling payload.zip ..."
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path "$appDir\*" -DestinationPath $payloadZip -CompressionLevel Optimal
Copy-Item $payloadZip (Join-Path $setupDir "payload.zip") -Force
$payloadMb = [math]::Round((Get-Item $payloadZip).Length / 1MB, 1)
Write-Host "    payload.zip: $payloadMb MB"

# ---- 4. publish installer exe (single-file, self-contained, payload embedded) ----
Write-Host "==> [4/4] Publishing installer exe ..."
if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
dotnet publish $setupCsproj `
    -c $Config -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $setupOut
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed (exit $LASTEXITCODE)" }

$setupExe = Get-ChildItem $setupOut -Filter "*.exe" | Select-Object -First 1
if (-not $setupExe) { throw "Installer exe not found in $setupOut" }
Copy-Item $setupExe.FullName $finalExe -Force

$mb = [math]::Round((Get-Item $finalExe).Length / 1MB, 1)
Write-Host "==> Done: $finalExe ($mb MB)"
