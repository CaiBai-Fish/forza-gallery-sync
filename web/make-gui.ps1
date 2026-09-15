# make-gui.ps1 - Build the Forza Gallery Sync GUI program (direct output)
#
# Pipeline:
#   1. Make sure web\python-runtime.zip exists (make-runtime.ps1). The archive is
#      embedded into the app assembly; the app extracts it on first run.
#   2. Publish the WinUI 3 app as a self-contained folder (exe + .NET runtime +
#      Windows App SDK runtime) -> web\dist\ForzaGallerySync-<version>-win-x64\
#   3. Write app-files.txt (release manifest) into the published folder. At first
#      run the app uses it to detect whether it runs from a clean directory.
#   4. Zip the folder -> web\dist\ForzaGallerySync-<version>-win-x64.zip
#
# There is no installer any more: the GUI program provisions the Python runtime
# itself (see Services\PythonHost.cs):
#   - clean program directory (only release files) -> <program dir>\python
#   - otherwise                                    -> %LOCALAPPDATA%\Programs\ForzaGallerySync\python
#
# Usage (in web dir):
#   powershell -ExecutionPolicy Bypass -File .\make-gui.ps1 [-PythonEnv <dir>] [-Version <x.y.z>] [-NoZip] [-ForceRuntime]
# Output: web\dist\ForzaGallerySync-<version>-win-x64\ (and .zip)
param(
    [string]$Config = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.5.0",
    [string]$PythonEnv = "",
    [switch]$NoZip,
    [switch]$ForceRuntime
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path        # web/
$projectRoot = Split-Path -Parent $root                        # repo root
$appCsproj = Join-Path $root "ForzaGallerySync.csproj"
$distDir = Join-Path $root "dist"
$outDir = Join-Path $distDir "ForzaGallerySync-$Version-$Runtime"
$outZip = "$outDir.zip"
$runtimeZip = Join-Path $root "python-runtime.zip"
$manifestName = "app-files.txt"

if (-not (Test-Path $appCsproj)) { throw "App project not found: $appCsproj" }

function Remove-DirWithRetry([string]$dir) {
    for ($i = 0; $i -lt 8; $i++) {
        try {
            if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop }
            return
        } catch {
            Start-Sleep -Milliseconds 1500
        }
    }
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- 1. embedded Python runtime (python-runtime.zip) ----
# Regenerate when missing, when -ForceRuntime is given, or when forza_sync sources /
# requirements / the runtime script itself are newer than the archive.
$needRuntime = $ForceRuntime -or (-not (Test-Path $runtimeZip))
if (-not $needRuntime) {
    $zipTime = (Get-Item $runtimeZip).LastWriteTimeUtc
    $newest = Get-ChildItem (Join-Path $projectRoot "forza_sync") -Recurse -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newest -and $newest.LastWriteTimeUtc -gt $zipTime) {
        $needRuntime = $true
    }
    foreach ($dep in @((Join-Path $projectRoot "requirements.txt"), (Join-Path $root "make-runtime.ps1"))) {
        if ((Get-Item $dep).LastWriteTimeUtc -gt $zipTime) {
            $needRuntime = $true
            break
        }
    }
}

if ($needRuntime) {
    Write-Host "==> [1/4] Generating python-runtime.zip (embedded runtime) ..."
    if (-not $PythonEnv) {
        $pyCmd = Get-Command python -ErrorAction SilentlyContinue
        if ($pyCmd) {
            $PythonEnv = Split-Path $pyCmd.Source -Parent
            Write-Host "    Auto-detected PythonEnv: $PythonEnv (from PATH)"
        } else {
            throw "Python environment not found: pass -PythonEnv <dir> or ensure python is on PATH."
        }
    }
    & (Join-Path $root "make-runtime.ps1") -PythonEnv $PythonEnv
    if ($LASTEXITCODE -ne 0) { throw "make-runtime failed (exit $LASTEXITCODE)" }
} else {
    $mb = [math]::Round((Get-Item $runtimeZip).Length / 1MB, 1)
    Write-Host "==> [1/4] Reusing python-runtime.zip ($mb MB)"
}

# ---- 2. publish the GUI as a self-contained folder ----
Write-Host "==> [2/4] Publishing GUI (self-contained folder) -> $outDir"
Remove-DirWithRetry $outDir
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
# -Version comes from pyproject.toml (single source of truth, passed by CI or the release script).
# Inject it into the assembly info so the exe properties show the real version; the workflow
# reads FileVersionInfo back and asserts it matches pyproject (guards against mismatched packages).
dotnet publish $appCsproj `
    -c $Config -r $Runtime -p:Platform=x64 `
    -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version `
    --self-contained true -o $outDir
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed (exit $LASTEXITCODE)" }

# The published assembly version must match -Version.
$exePath = Join-Path $outDir "forza-gallery-sync.exe"
if (Test-Path $exePath) {
    $info = (Get-Item $exePath).VersionInfo
    $embedded = ($info.FileVersion -split '\.')[0..2] -join '.'
    Write-Host "    assembly version: FileVersion=$($info.FileVersion) ProductVersion=$($info.ProductVersion)"
    if ($embedded -ne $Version) {
        throw "Assembly version mismatch: FileVersion=$($info.FileVersion) vs -Version=$Version"
    }
}

# ---- 3. release manifest (used by the app for clean-directory detection) ----
Write-Host "==> [3/4] Writing release manifest $manifestName ..."
$files = Get-ChildItem $outDir -Recurse -File |
    ForEach-Object { $_.FullName.Substring($outDir.Length + 1).Replace('\', '/') } |
    Sort-Object
[IO.File]::WriteAllLines(
    (Join-Path $outDir $manifestName),
    [string[]]$files,
    (New-Object System.Text.UTF8Encoding($false)))
Write-Host "    $($files.Count) files"

# ---- 4. zip the folder (keeps the versioned folder as the archive root) ----
if (-not $NoZip) {
    Write-Host "==> [4/4] Compressing $outZip ..."
    if (Test-Path $outZip) { Remove-Item $outZip -Force }
    Compress-Archive -Path $outDir -DestinationPath $outZip -CompressionLevel Optimal
    $mb = [math]::Round((Get-Item $outZip).Length / 1MB, 1)
    Write-Host "==> Done: $outZip ($mb MB)"
} else {
    Write-Host "==> [4/4] Skipped zip (-NoZip)"
}

$sizeMb = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "==> Done: $outDir ($sizeMb MB)"
exit 0
