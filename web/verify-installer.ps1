# ============================================================
#  Forza Gallery Sync - 安装程序自动化验证
#
#  自动验证（不需要人工点击）：
#    - 静默安装：退出码 0、关键文件在位（exe / Assets\app-icon.png / 卸载器）
#    - 静默卸载：退出码 0、目录删干净、日志无异常
#    - 交互卸载的自定义窗体确实弹出，且控件齐全（按钮 / 复选框 / 文案）
#    - 点"取消"中止卸载时，用户数据必须保留
#
#  ⚠️ **勾选"删除用户数据"这条分支不做自动化**：
#     Inno 的 TSetupForm 控件没有暴露 UI Automation 的 Invoke/Toggle 模式
#     （实测窗口里元素都是 Pane 类型，取不到 TogglePattern），要自动化只能按坐标
#     做底层鼠标点击——而这条路径会真的删掉 %APPDATA%\forza-sync
#     （登录凭据 + 照片索引库）。拿真实用户数据去赌一次坐标点击不划算，
#     这条分支由人在自己机器上手动确认（脚本末尾会打印步骤）。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File .\web\verify-installer.ps1 `
#      -SetupPath web\dist\ForzaGallerySync-1.0.3-setup.exe
#
#  安全说明：脚本会在 %APPDATA%\forza-sync 下临时放一个**哨兵文件**用于断言数据是否被保留。
#  该目录已存在时会先整目录备份、结束后还原；不存在则只在结束时清掉自己建的哨兵。
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [string]$TestRoot = ""
)
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace W -Name W -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
'@

if (-not $TestRoot) { $TestRoot = Join-Path $env:TEMP "fgs-verify-installer" }
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$userData = Join-Path $env:APPDATA "forza-sync"
$sentinel = Join-Path $userData "verify-sentinel.txt"
$backup = Join-Path $TestRoot "userdata-backup"

$failures = @()
function Assert([bool]$condition, [string]$message) {
    if ($condition) { Write-Host "  [PASS] $message" }
    else { Write-Host "  [FAIL] $message" -ForegroundColor Red; $script:failures += $message }
}

function Get-UninstallerWindow {
    # 按标题找卸载器窗口（不依赖 PID：实际承载窗口的进程是临时目录里的 _unins.tmp）
    $script:foundHwnd = [IntPtr]::Zero
    $cb = [W.W+EnumProc]{
        param($h, $l)
        if ([W.W]::IsWindowVisible($h) -and [W.W]::GetWindowTextLength($h) -gt 0) {
            $sb = New-Object System.Text.StringBuilder 300
            [void][W.W]::GetWindowText($h, $sb, 300)
            if ($sb.ToString() -like '卸载*') { $script:foundHwnd = $h; return $false }
        }
        return $true
    }
    [void][W.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:foundHwnd
}

function Wait-UninstallerWindow([int]$timeoutSec = 25) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $h = Get-UninstallerWindow
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 400
    }
    return [IntPtr]::Zero
}

function Stop-StrayUninstallers {
    Get-Process | Where-Object { $_.ProcessName -match 'unins|_unins' } | ForEach-Object { $_.Kill() }
    Start-Sleep -Milliseconds 800
}

function Remove-InstallLeftovers([string[]]$dirs) {
    foreach ($d in $dirs) {
        if (Test-Path $d) {
            $un = Join-Path $d "unins000.exe"
            if (Test-Path $un) {
                Start-Process -FilePath $un -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART" -Wait | Out-Null
            }
            if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall" -ErrorAction SilentlyContinue |
        Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -like '*Forza Gallery Sync*' } |
        ForEach-Object { Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }
    Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*Forza Gallery Sync*' } |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

function Click-Element($element) {
    $pt = $element.GetClickablePoint()
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$pt.X, [int]$pt.Y)
    Start-Sleep -Milliseconds 200
    [W.W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # left down
    [W.W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # left up
}

$origData = Test-Path $userData
try {
    Write-Host "== 准备：备份现有用户数据（结束时还原）=="
    if (Test-Path $TestRoot) { Remove-Item $TestRoot -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $TestRoot -Force | Out-Null
    if ($origData) {
        Copy-Item $userData $backup -Recurse -Force
        Write-Host "  已备份到 $backup"
    } else {
        Write-Host "  本机当前没有用户数据，无需备份"
    }
    Stop-StrayUninstallers

    Write-Host ""
    Write-Host "== 场景 1：静默安装 / 静默卸载 =="
    $dir1 = Join-Path $TestRoot "silent"
    $p = Start-Process -FilePath $setup -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/DIR=$dir1" -PassThru -Wait
    Assert ($p.ExitCode -eq 0) "静默安装退出码 0（实际 $($p.ExitCode)）"
    Assert (Test-Path (Join-Path $dir1 "forza-gallery-sync.exe")) "安装后有 forza-gallery-sync.exe"
    Assert (Test-Path (Join-Path $dir1 "Assets\app-icon.png")) "安装后有 Assets\app-icon.png"
    Assert ((Get-ChildItem $dir1 -Filter 'unins*.exe' -ErrorAction SilentlyContinue).Count -ge 1) "安装后有卸载器 unins000.exe"

    $log1 = Join-Path $TestRoot "uninstall-silent.log"
    $u = Start-Process -FilePath (Join-Path $dir1 "unins000.exe") `
        -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/LOG=$log1" -PassThru -Wait
    Assert ($u.ExitCode -eq 0) "静默卸载退出码 0（实际 $($u.ExitCode)）"
    Assert (-not (Test-Path $dir1)) "静默卸载后程序目录已删除"
    if (Test-Path $log1) {
        $ex = Select-String -Path $log1 -Pattern 'exception|Access violation' -Encoding UTF8
        Assert ($null -eq $ex) "静默卸载日志无异常（历史上出现过窗体释放后访问控件导致 Access violation）"
    }

    Write-Host ""
    Write-Host "== 场景 2：交互卸载窗体弹出、控件齐全、取消时数据保留 =="
    New-Item -ItemType Directory -Path $userData -Force | Out-Null
    [IO.File]::WriteAllText($sentinel, "sentinel")
    Assert (Test-Path $sentinel) "已放置哨兵文件（用于判断数据是否被保留）"

    $dir2 = Join-Path $TestRoot "keep"
    $p = Start-Process -FilePath $setup -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/DIR=$dir2" -PassThru -Wait
    Assert ($p.ExitCode -eq 0) "安装（场景 2）退出码 0"

    $u = Start-Process -FilePath (Join-Path $dir2 "unins000.exe") -PassThru
    $hwnd = Wait-UninstallerWindow
    Assert ($hwnd -ne [IntPtr]::Zero) "卸载器自定义窗体已弹出"
    if ($hwnd -ne [IntPtr]::Zero) {
        $win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $elements = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $names = @($elements | ForEach-Object { $_.Current.Name })

        Assert ($names -contains "继续卸载") "窗体有『继续卸载』按钮"
        Assert ($names -contains "取消") "窗体有『取消』按钮"
        Assert (($names | Where-Object { $_ -like '*同时删除上述用户数据*' }).Count -ge 1) "窗体有『删除用户数据』复选框"
        Assert (($names | Where-Object { $_ -like '*forza-sync*' }).Count -ge 1) "窗体写明了数据实际位置"
        Assert (($names | Where-Object { $_ -like '*照片文件*' }).Count -ge 1) "窗体说明了照片文件不受影响"

        # 点"取消"→ 卸载中止，数据必须保留（这条能自动断言，且不删数据）
        $cancel = $elements | Where-Object { $_.Current.Name -eq "取消" } | Select-Object -First 1
        if ($cancel) {
            Click-Element $cancel
            Start-Sleep -Seconds 3
        }
        Assert (Test-Path $sentinel) "点取消中止卸载后，用户数据仍在"
    }
    Stop-StrayUninstallers
}
finally {
    Write-Host ""
    Write-Host "== 清理与还原 =="
    Stop-StrayUninstallers
    Remove-InstallLeftovers @((Join-Path $TestRoot "silent"), (Join-Path $TestRoot "keep"))

    if (Test-Path $backup) {
        if (Test-Path $userData) { Remove-Item $userData -Recurse -Force -ErrorAction SilentlyContinue }
        Copy-Item $backup $userData -Recurse -Force
        Write-Host "  已还原用户数据（$userData）"
    } elseif (Test-Path $userData) {
        Remove-Item $sentinel -Force -ErrorAction SilentlyContinue
        if (-not (Get-ChildItem $userData -Force -ErrorAction SilentlyContinue)) {
            Remove-Item $userData -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  已清理测试目录、注册表项与快捷方式"
}

Write-Host ""
Write-Host "手动确认项（不做自动化，原因见文件头）：" -ForegroundColor Yellow
Write-Host "  1) 运行安装程序 → 卸载时勾选『同时删除上述用户数据（不可恢复）』→ 点『继续卸载』"
Write-Host "  2) 检查 %APPDATA%\forza-sync 已不存在"
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "自动断言全部通过" -ForegroundColor Green
    exit 0
} else {
    Write-Host "失败 $($failures.Count) 项：" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
