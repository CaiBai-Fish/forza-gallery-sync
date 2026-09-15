# ============================================================
#  Forza Gallery Sync - 生成增量更新资产与清单
#
#  用途：把发布目录里"体积大、且相对上一版真正变化"的文件各自压成单文件 zip，
#        并生成逐文件清单 increment.json（客户端据此判断本地哪些文件需要更新）。
#
#  为什么这么做：发布目录解压后约 269 MB，其中 Python 运行时 77 MB、
#  .NET / Windows SDK 运行时 182 MB，这些跨版本几乎不变；每次版本更新真正变的
#  只有应用自身那几个文件。客户端只下载变化的文件，就不必每次重下 130 MB 的完整包。
#
#  关键取舍（实测数据）：如果给所有 ≥ 阈值的大文件都发资产，110 个资产合计 92 MB
#  （完整包 130 MB），几乎失去增量意义。因此用 -PreviousManifest 对比上一版清单，
#  只给"上一版没有或内容变了"的文件发资产。跨版本不变的大文件不发资产——
#  客户端本地已有正确内容时不会下载它；万一本地缺失（例如用户手工删过文件），
#  客户端会回退完整包，结果依然正确。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File .\web\make-increment.ps1 `
#      -SourceDir web\dist\ForzaGallerySync-1.0.4-win-x64 -Version 1.0.4 `
#      -OutputDir $env:TEMP\inc `
#      -PreviousManifest https://raw.githubusercontent.com/.../hashes/increment.json
#
#  产物：<OutputDir>\ForzaGallerySync-inc-*.zip（需要独立资产的文件各一个）
#        <OutputDir>\increment-<版本>.json（权威清单，含全部文件）
#
#  清单是对客户端的契约：
#    version      目标版本
#    threshold    低于该体积的文件不提供独立资产，客户端遇到差异时回退完整包
#    artifactPath 完整包解压后的顶层目录名（增量包按同样结构组织）
#    files[]      path / size / sha256，需要独立资产的文件另有 name（资产名）与 zipped（资产体积）
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDir = "",
    [int]$Threshold = 262144,   # 256KB
    [string]$PreviousManifest = ""
)
$ErrorActionPreference = "Stop"

$src = (Resolve-Path -LiteralPath $SourceDir).Path
if (-not $OutputDir) { $OutputDir = Join-Path $env:TEMP "fgs-inc-$Version" }
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$out = (Resolve-Path -LiteralPath $OutputDir).Path
$artifactPath = Split-Path $src -Leaf

# 与客户端同一算法：路径里的分隔符换成 '_'，得到 Release 资产名
function Get-AssetName([string]$relative) {
    return 'ForzaGallerySync-inc-' + $relative.Replace('\', '_').Replace('/', '_')
}

# 上一版清单：path -> sha256
$prev = @{}
if ($PreviousManifest) {
    try {
        if ($PreviousManifest -match '^https?://') {
            $json = Invoke-RestMethod -Uri $PreviousManifest -Headers @{ 'User-Agent' = 'forza-sync-ci' } -TimeoutSec 20
        } elseif (Test-Path -LiteralPath $PreviousManifest) {
            $json = Get-Content -LiteralPath $PreviousManifest -Raw -Encoding utf8 | ConvertFrom-Json
        } else {
            throw "上一版清单既不是 URL 也不存在：$PreviousManifest"
        }
        foreach ($f in $json.files) { $prev[$f.path] = $f.sha256 }
        Write-Host ("上一版清单载入 {0} 条记录（版本 {1}）" -f $prev.Count, $json.version)
    } catch {
        Write-Host ("[warn] 载入上一版清单失败，本次改为给全部大文件发资产：{0}" -f $_.Exception.Message)
        $prev = @{}
    }
} else {
    Write-Host "[info] 未提供上一版清单：本次给全部大文件发资产"
}

$allFiles = Get-ChildItem -LiteralPath $src -Recurse -File
$changed = @()
$smallChanged = @()   # 变化的小文件（体积低于阈值），最后打成一个容器包
$unchanged = 0
$files = @()

foreach ($f in $allFiles) {
    $rel = $f.FullName.Substring($src.Length).TrimStart('\', '/').Replace('\', '/')

    # app-files.txt 是"文件集合"的产物，且用于便携布局检测，不参与内容比对
    if ($rel -eq 'app-files.txt') { continue }

    $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLower()
    $record = [ordered]@{
        path   = $rel
        size   = $f.Length
        sha256 = $hash
    }

    $isBig = $f.Length -ge $Threshold
    # 相对上一版是否变化（没给上一版清单时一律视为变化）
    $isChanged = $prev.Count -eq 0 -or -not $prev.ContainsKey($rel) -or $prev[$rel] -ne $hash

    if ($isBig -and $isChanged) {
        # 大文件：一个文件一个资产，客户端只下需要的那几个
        $asset = Get-AssetName $rel
        $zipPath = Join-Path $out "$asset.zip"
        Compress-Archive -LiteralPath $f.FullName -DestinationPath $zipPath -CompressionLevel Optimal -Force
        $record['name'] = $asset
        $record['zipped'] = (Get-Item -LiteralPath $zipPath).Length
        $changed += $record
    } elseif ($isBig) {
        $unchanged++
    } elseif ($isChanged) {
        # 小文件：单独发资产会有几百个（GitHub 上限与 Release 页面都受不了），
        # 但也不能不管——任何一个变化的小文件都会让增量整体失效、回退完整包。
        # 折中：把变化的小文件打成**一个**包（见下面的 containers）。
        $smallChanged += $f
    }

    $files += $record
}

if ($files.Count -eq 0) { throw "发布目录里没有可比对的文件" }

# 变化的小文件 → 单个资产；容器记录告诉客户端这个包里有哪些文件
$containers = @()
if ($smallChanged.Count -gt 0) {
    $smallAsset = "ForzaGallerySync-inc-small-$Version"
    $smallZip = Join-Path $out "$smallAsset.zip"
    $smallStage = Join-Path $out ".small-stage"
    if (Test-Path -LiteralPath $smallStage) { Remove-Item -LiteralPath $smallStage -Recurse -Force }
    New-Item -ItemType Directory -Path $smallStage -Force | Out-Null

    $paths = New-Object System.Collections.ArrayList
    foreach ($item in $smallChanged) {
        $full = [string]$item.FullName
        $rel = $full.Substring($src.Length).TrimStart('\', '/')
        $relNative = $rel.Replace('/', '\')
        $dst = Join-Path $smallStage $relNative
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
        Copy-Item -LiteralPath $full -Destination $dst -Force
        [void]$paths.Add($rel.Replace('\', '/'))
    }

    # 目录整体压缩：比逐文件压缩路径更可预期，且保持包内相对结构
    if (Test-Path -LiteralPath $smallZip) { Remove-Item -LiteralPath $smallZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $smallStage, $smallZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item -LiteralPath $smallStage -Recurse -Force -ErrorAction SilentlyContinue

    $containers += [ordered]@{
        name   = $smallAsset
        kind   = 'small-files'
        zipped = (Get-Item -LiteralPath $smallZip).Length
        files  = $paths.ToArray()
    }
}

$manifest = [ordered]@{
    version      = $Version
    threshold    = $Threshold
    artifactPath = $artifactPath
    generated    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    files        = $files
    containers   = $containers
}

$manifestPath = Join-Path $out "increment-$Version.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6),
    (New-Object System.Text.UTF8Encoding($false)))

# 统计：用 foreach 累加，避免 Measure-Object 依赖哈希表的属性枚举
$incZip = 0L
$incRaw = 0L
foreach ($e in $changed) { $incZip += [int64]$e['zipped']; $incRaw += [int64]$e['size'] }
foreach ($c in $containers) { $incZip += [int64]$c['zipped'] }
$allRaw = 0L
foreach ($e in $files) { $allRaw += [int64]$e['size'] }

Write-Host ("增量资产 {0} 个 + 小文件包 {1} 个（压缩后合计 {2:N1} MB，大文件原始 {3:N1} MB）" -f `
    $changed.Count, $containers.Count, ($incZip / 1MB), ($incRaw / 1MB))
Write-Host ("跨版本未变化的大文件 {0} 个：不发资产，客户端本地已有就不会下载" -f $unchanged)
Write-Host ("清单 {0} 个条目（全部 {1:N1} MB）-> {2}" -f $files.Count, ($allRaw / 1MB), $manifestPath)
Write-Host ("资产目录 -> {0}" -f $out)
exit 0
