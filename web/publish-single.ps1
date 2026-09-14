# publish-single.ps1 - [DEPRECATED] alias kept for compatibility
#
# Since 0.5.0 the release build outputs the GUI program directly (self-contained
# folder + zip); the app extracts the Python runtime itself on first run.
# Use instead:
#   powershell -ExecutionPolicy Bypass -File .\make-gui.ps1
# Output: web\dist\ForzaGallerySync-<version>-win-x64\ (and .zip)
param(
    [string]$Config = "Release",
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"

Write-Host "==> publish-single.ps1 is deprecated; delegating to make-gui.ps1 ..."
& (Join-Path $PSScriptRoot "make-gui.ps1") -Config $Config -Runtime $Runtime
exit $LASTEXITCODE

