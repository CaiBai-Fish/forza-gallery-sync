# ============================================================
#  Forza Gallery Sync - 运行安装程序完成更新
#
#  由应用（UpdateService.LaunchInstaller）生成并启动。
#  生成时会替换脚本正文里的占位符（安装程序路径 / 日志路径 / 安装模式 / 目录 / 应用目录 / PID）。
#  注意：这里**不要**把占位符名原样写进注释——替换是纯文本的，注释里的也会被一起换掉，
#  结果这行说明变成一串路径（实测踩过）。
#
#  为什么需要这个脚本，而不是让应用自己直接运行安装程序：
#   1) 安装程序要覆盖正在运行的 exe/dll，而应用无法自删除式地"边跑边被替换"；
#   2) 应用必须先退出，但退出后就没有人负责"装完再启动新版本"了。
#  所以由这个脚本当调度者：等应用退出 → 静默安装 → 按结果决定是否重启。
#
#  安装模式（由应用决定后传进来）：
#   - portable：免安装版（zip 解压运行）→ 带 /DIR=<应用当前目录>，装回原处，
#               这样"原本免安装"的用户升级后依然在同一个目录里；
#   - installed：安装版 → 不带 /DIR，让 Inno 用它记录的安装目录，避免同一版本
#                出现两个安装位置。
#
#  /SILENT（而非 /VERYSILENT）：显示安装进度窗口，用户能看到"正在装"而不是一片黑；
#  且静默模式下 Inno 的 CloseApplications 会自动处理占用文件的进程、也不会弹
#  "是否重启计算机"。
# ============================================================

$ErrorActionPreference = 'Continue'

$installer = '{{INSTALLER}}'
$log       = '{{LOG}}'
$mode      = '{{MODE}}'
$dir       = '{{DIR}}'
$appExe    = '{{APP}}\forza-gallery-sync.exe'
$oldPid    = {{PID}}

function Log([string]$message) {
    Add-Content -LiteralPath $log -Value ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $message)
}

Set-Content -LiteralPath $log -Value '=== Forza Gallery Sync 安装更新 ===' -Encoding Default
Log ('模式: {0}；安装程序: {1}' -f $mode, $installer)

if (-not (Test-Path -LiteralPath $installer)) {
    Log '失败：找不到安装程序文件，未做任何更改'
    exit 1
}

# 1) 等应用退出：它占用着要被替换的 exe/dll
#    用 tasklist 按 PID 查询，比按进程名更准确（同一机器可能有别的实例）
Log ('等待应用退出 (PID {0})' -f $oldPid)
$waited = 0.0
while ($waited -lt 60) {
    $found = tasklist /FI ('PID eq {0}' -f $oldPid) 2>$null | Select-String -SimpleMatch ('{0}' -f $oldPid)
    if (-not $found) { break }
    Start-Sleep -Milliseconds 500
    $waited += 0.5
}
if ($waited -ge 60) {
    Log '等待超时，仍继续尝试安装（可能部分文件被占用）'
} else {
    Log ('应用已退出，耗时 {0} 秒' -f $waited)
}

# 2) 组装安装参数
$args = @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS', ('/LOG=' + $log + '.setup'))
if ($mode -eq 'portable' -and $dir) {
    $args += ('/DIR=' + $dir)
    Log ('免安装版：装回原目录 {0}' -f $dir)
} else {
    Log '安装版：使用 Inno 记录的安装目录'
}

# 3) 运行安装程序（它是同步的，装完才返回）
Log '开始安装…'
$proc = Start-Process -FilePath $installer -ArgumentList $args -PassThru -Wait
$code = $proc.ExitCode
Log ('安装程序退出码: {0}' -f $code)

# Inno 退出码：0 = 成功；非 0 见 https://jrsoftware.org/ishelp/index.php?topic=setupexitcodes
# 常见：1 = 用户取消（/SILENT 下也会出现，例如文件被占用无法替换）；
#       2 = 致命错误（安装前）；5 = 用户点了取消按钮；1602 = 用户取消；
#       1603 = 致命错误（安装中）—— 这类都意味着**没有装成**。
$success = ($code -eq 0 -or $code -eq 3010)

if ($success) {
    Log '安装成功，启动新版本'
    # 清理安装包：这次用不到就删，失败时留着让用户手动重试
    Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $appExe
    Log ('已启动 {0}' -f $appExe)
} else {
    Log '安装未完成（可能被取消或失败），将启动原版本'
    Log ('安装包保留在: {0}' -f $installer)
    if (Test-Path -LiteralPath $appExe) {
        Start-Process -FilePath $appExe
        Log '已启动原版本'
    }
}

Log '完成'

# 自删除
Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
