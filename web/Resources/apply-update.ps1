# ============================================================
#  Forza Gallery Sync - 自动更新替换脚本
#
#  由应用（UpdateService.LaunchReplaceAndRestart）生成并启动。
#  占位符 {{APP}} / {{ZIP}} / {{PREFIX}} / {{STAGE}} / {{LOG}} / {{PID}} / {{IDENTITY}}
#  在启动前由 C# 侧替换为实际值。
#
#  流程：等旧进程退出 → 解压更新包 → 覆盖程序目录中的文件 → 重启应用
#  设计要点：
#    - 必须等旧进程退出：运行中的 exe / dll 被占用，无法覆盖。
#    - 只覆盖、不删除：多余文件（用户自己的东西）保留，
#      也避免删错文件导致程序无法启动。
#    - 覆盖失败不静默：全部写入日志，并提示手动下载覆盖。
# ============================================================

$ErrorActionPreference = 'Continue'

$app      = '{{APP}}'
$zip      = '{{ZIP}}'
$prefix   = '{{PREFIX}}'
$stage    = '{{STAGE}}'
$log      = '{{LOG}}'
$oldPid   = {{PID}}
$identity = '{{IDENTITY}}'
$exe      = Join-Path $app 'forza-gallery-sync.exe'
$artifact = 'forza-gallery-sync.exe'

function Log([string]$message) {
    Add-Content -LiteralPath $log -Value ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $message)
}

Set-Content -LiteralPath $log -Value '=== Forza Gallery Sync 自动更新 ===' -Encoding Default
Log ('等待旧进程退出 (PID {0}, 身份 {1})' -f $oldPid, $identity)

# 用 tasklist 按 PID 查询：即使旧进程完整性级别更高也能查到。
# 最多等 60 秒，避免旧进程异常时永远卡住。
$waited = 0.0
while ($waited -lt 60) {
    $found = tasklist /FI ('PID eq {0}' -f $oldPid) 2>$null | Select-String -SimpleMatch ('{0}' -f $oldPid)
    if (-not $found) { break }
    Start-Sleep -Milliseconds 500
    $waited += 0.5
}
if ($waited -ge 60) {
    Log '等待超时，仍继续尝试（可能部分文件被占用）'
} else {
    Log ('旧进程已退出，耗时 {0} 秒' -f $waited)
}

try {
    Log ('解压更新包到 {0}' -f $stage)
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force -ErrorAction Stop

    # 发布包形如 <顶层目录>/forza-gallery-sync.exe；剥离前缀后定位真正的源目录
    $src = Join-Path $stage $prefix
    if (-not (Test-Path -LiteralPath (Join-Path $src $artifact))) { $src = $stage }
    if (-not (Test-Path -LiteralPath (Join-Path $src $artifact))) {
        throw ('解压后找不到 {0}' -f $artifact)
    }
    Log ('源目录 {0}' -f $src)

    $copied = 0
    $failed = @()
    Get-ChildItem -LiteralPath $src -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length).TrimStart('\')
        $dst = Join-Path $app $rel
        $dstDir = Split-Path $dst -Parent
        try {
            if (-not (Test-Path -LiteralPath $dstDir)) {
                New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $_.FullName -Destination $dst -Force -ErrorAction Stop
            $copied++
        } catch {
            $failed += ('{0} : {1}' -f $rel, $_.Exception.Message)
        }
    }

    Log ('已覆盖 {0} 个文件' -f $copied)
    if ($failed.Count -gt 0) {
        Log '以下文件覆盖失败（程序可能处于新旧混合状态，建议重新下载覆盖）：'
        $failed | ForEach-Object { Log ('  ' + $_) }
    }

    Log '重启应用'
    Start-Process -FilePath $exe -WorkingDirectory $app
    Log '完成'
} catch {
    Log ('失败：' + $_.Exception.Message)
    Log ('请手动下载更新包并解压覆盖 {0}' -f $app)
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
}

# 自删除
Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
