# publish-single.ps1 - [DEPRECATED] old single-file exe publish
#
# Since 0.4.0 the build produces an installer instead: it extracts the
# Python/.NET runtimes into the install directory, and the app uses them
# directly from there. Use instead:
#   powershell -ExecutionPolicy Bypass -File .\make-installer.ps1
# Output: web\dist\ForzaGallerySync-Setup-<version>.exe
param(
    [string]$Config = "Release",
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"

Write-Host "==> publish-single.ps1 is deprecated; delegating to make-installer.ps1 ..."
& (Join-Path $PSScriptRoot "make-installer.ps1") -Config $Config -Runtime $Runtime
exit $LASTEXITCODE

