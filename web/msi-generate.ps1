# msi-generate.ps1 - Generate a WiX v4 component manifest (app.wxs) from an
# application directory. Every file becomes its own Component (heat-style)
# with a deterministic MD5-based GUID (stable across builds), so MSI upgrades
# treat unchanged files as the same component.
#
# Usage:
#   .\msi-generate.ps1 -AppDir <abs path to app folder> -OutWxs <abs path to output .wxs>
param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][string]$OutWxs
)
$ErrorActionPreference = "Stop"

function ConvertTo-EscapedXml([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return "" }
    return [System.Security.SecurityElement]::Escape($s)
}

# Deterministic UUIDv5-style GUID from a seed string.
function New-DeterministicGuid([string]$seed) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("fgs-msi-v1:$seed"))
    $bytes[6] = ($bytes[6] -band 0x0F) -bor 0x50
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    return (New-Object System.Guid (, [byte[]]$bytes)).ToString("B").ToUpperInvariant()
}

# Stable identifier (prefix + first N hex chars of MD5 of the path).
function New-StableId([string]$prefix, [string]$path, [int]$len = 12) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($path))
    $hex = [System.BitConverter]::ToString($bytes).Replace("-", "").ToLowerInvariant()
    return $prefix + $hex.Substring(0, $len)
}

$sb = New-Object System.Text.StringBuilder
$script:componentRefs = New-Object System.Collections.Generic.List[string]
$script:fileCount = 0

# Recursively emit Directory/Component XML for a folder.
function Emit-Directory([System.Text.StringBuilder]$sb, [string]$absPath, [string]$relPath, [int]$depth) {
    $ind = "  " * $depth
    $subs = @(Get-ChildItem -LiteralPath $absPath -Directory | Sort-Object Name)
    $files = @(Get-ChildItem -LiteralPath $absPath -File | Sort-Object Name)

    # files -> one component each, in this directory
    foreach ($f in $files) {
        $rel = if ($relPath) { "$relPath\$($f.Name)" } else { $f.Name }
        $cmpId = New-StableId "cmp_" $rel
        $filId = New-StableId "fil_" $rel
        $guid = New-DeterministicGuid $rel
        $script:fileCount++
        [void]$script:componentRefs.Add($cmpId)
        [void]$sb.AppendLine("$ind<Component Id=`"$cmpId`" Guid=`"$guid`">")
        [void]$sb.AppendLine("$ind  <File Id=`"$filId`" Source=`"$(ConvertTo-EscapedXml $f.FullName)`" />")
        [void]$sb.AppendLine("$ind</Component>")
    }

    # subdirectories
    foreach ($d in $subs) {
        $rel = if ($relPath) { "$relPath\$($d.Name)" } else { $d.Name }
        $dirId = New-StableId "dir_" $rel
        [void]$sb.AppendLine("$ind<Directory Id=`"$dirId`" Name=`"$(ConvertTo-EscapedXml $d.Name)`">")
        Emit-Directory $sb $d.FullName $rel ($depth + 1)
        [void]$sb.AppendLine("$ind</Directory>")
    }
}

if (-not (Test-Path -LiteralPath $AppDir)) { throw "AppDir not found: $AppDir" }

[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
Emit-Directory $sb $AppDir "" 3
[void]$sb.AppendLine('    </DirectoryRef>')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles">')
foreach ($id in $script:componentRefs) {
    [void]$sb.AppendLine("      <ComponentRef Id=`"$id`" />")
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$outDir = Split-Path -Parent $OutWxs
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
[System.IO.File]::WriteAllText($OutWxs, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "    app.wxs: $($script:fileCount) files, $($script:componentRefs.Count) components -> $OutWxs"
exit 0
