# ============================================================
#  Forza Gallery Sync - 发布目录校验
#
#  为什么是"校验"而不是"整理"：
#  语言资源的裁剪已经在**编译期**完成了 —— 见 web/ForzaGallerySync.csproj 里的
#  ExcludeUnusedWinUILanguages target（通过 Windows App SDK 官方的
#  MicrosoftWindowsAppSDKFilesExcluded 扩展点排除用不到的 .mui）。
#  所以 `dotnet publish` 直接产出精简结构，不需要打包后再删文件。
#
#  这个脚本保留下来做**发布前校验**：确认裁剪确实生效、所需的语言资源还在。
#
#  ⚠️ 不要改成"把文件移到 runtime/、resources/ 等子目录"：
#     这些路径是平台硬编码的，实测会直接崩溃（换 SDK 版本后建议复测）：
#       - 语言目录挪到 resources\lang\ → WinUI: COMException
#         「资源加载器缓存没有已加载的 MUI 项」，启动即崩
#       - 托管程序集挪到 runtime\      → .NET 主机在运行托管代码前退出
#         （0x80008009 / 0xE0434352，连日志都写不出来）
#     结论：自包含 WinUI 发布目录的**扁平布局是平台约束**，
#     只能靠"减少文件"（裁剪）改善，不能靠"重新分类"。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File .\web\organize-release.ps1 -TargetDir <发布目录>
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$TargetDir,
    [string[]]$KeepLanguages = @('zh-CN', 'zh-TW', 'en-us')
)
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $TargetDir).Path

# 语言目录形如 zh-CN / af-ZA / sr-Cyrl-RS / ca-Es-VALENCIA：
# 字母语言段 + 一到两段地区；用它把"语言目录"和产品自己的目录区分开。
$langPattern = '^[a-z]{2,3}(-[A-Za-z]{2,8}){1,2}$'

$langs = Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match $langPattern }
$unexpected = @($langs | Where-Object { $_.Name -notin $KeepLanguages })

$mui = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.mui)
$rootFiles = (Get-ChildItem -LiteralPath $root -File).Count

Write-Host ("语言目录 {0} 个（期望 {1}：{2}）" -f $langs.Count, $KeepLanguages.Count, ($KeepLanguages -join ', '))
Write-Host ("附属资源 .mui {0} 个；根目录 {1} 个文件" -f $mui.Count, $rootFiles)

$failed = $false

if ($unexpected.Count -gt 0) {
    Write-Host ("[FAIL] 存在未裁剪的语言目录：{0}" -f (($unexpected | Select-Object -First 6 | ForEach-Object { $_.Name }) -join ', '))
    Write-Host "       语言裁剪依赖 csproj 的 MicrosoftWindowsAppSDKFilesExcluded 排除项，请检查是否失效。"
    $failed = $true
}

# 必需的语言资源必须在根目录且有 .mui（WinUI 按 <exe目录>\<语言>\*.mui 查找，不能挪）
foreach ($keep in $KeepLanguages) {
    $path = Join-Path $root $keep
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host ("[FAIL] 缺少语言目录 {0}（WinUI 需要它加载该语言资源）" -f $keep)
        $failed = $true
    } elseif (-not (Get-ChildItem -LiteralPath $path -File -Filter *.mui -ErrorAction SilentlyContinue)) {
        Write-Host ("[FAIL] {0} 下没有 .mui 文件" -f $keep)
        $failed = $true
    }
}

# 应用自身文件必须在根目录（运行时按固定路径找它们）
foreach ($required in @('forza-gallery-sync.exe', 'forza-gallery-sync.dll', 'resources.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required))) {
        Write-Host ("[FAIL] 根目录缺少 {0}" -f $required)
        $failed = $true
    }
}

if ($failed) { throw "发布目录校验失败（见上面 [FAIL] 项）" }

Write-Host "发布目录校验通过"
exit 0
