"""把 C# 里面向用户的文案接上 StringLocalizer（分批推进）。

策略说明（为什么不是全量自动替换）：
  界面文案与"日志/诊断文案"必须分开处理 —— 日志是给开发者看的，翻译只会让检索更难。
  所以这里维护一张**显式清单**：只处理确实会显示给用户的字符串，逐条给定键名与英文译文，
  然后按键名把 C# 里的字面量替换成 StringLocalizer 调用。

用法：
  python tools/i18n_apply_cs.py --check   只检查清单里的键是否都在 resw 中
  python tools/i18n_apply_cs.py           写 resw 并替换 C# 字面量
"""
from __future__ import annotations

import io
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"

# key -> (简体中文, English)
# 键名规则：模块_用途（可读、稳定，便于以后查找）
STRINGS: dict[str, tuple[str, str]] = {
    # ---- MainWindow：标题栏状态与账号状态 ----
    "Main_Title": ("Forza Gallery Sync 控制台", "Forza Gallery Sync Console"),
    "Main_Status_Syncing_Count": ("同步中 {0}/{1}", "Syncing {0}/{1}"),
    "Main_Status_Syncing": ("同步中", "Syncing"),
    "Main_Status_Cancelling": ("正在取消", "Cancelling"),
    "Main_Account_SignedIn": ("已登录", "Signed in"),
    "Main_Account_TokenExpired": ("Token 已过期", "Token expired"),
    "Main_Account_NotSignedIn": ("未登录", "Not signed in"),

    # ---- Dashboard ----
    "Dash_Hero_NoPhotos": ("尚未同步任何照片", "No photos synced yet"),
    "Dash_Token_NotConfigured": ("未配置", "Not configured"),
    "Dash_Hero_NotSynced": ("尚未同步任何照片，先登录并执行一次同步",
                            "No photos yet — sign in and run a sync"),
    "Dash_Hero_Summary": ("来自 {0} 个游戏 · 覆盖 {1} 个月份",
                          "From {0} games · covering {1} months"),
    "Dash_Token_Missing": ("未配置 Token", "Token not configured"),
    "Dash_Token_Expired": ("Token 已过期", "Token expired"),
    "Dash_Token_Valid": ("Token 有效", "Token valid"),
    "Dash_Card_TotalPhotos": ("照片总数", "Total photos"),
    "Dash_Card_CoverMonths": ("覆盖 {0} 个月", "Covering {0} months"),
    "Dash_Card_NoData": ("暂无数据", "No data"),
    "Dash_Card_SyncedGames": ("已同步游戏", "Synced games"),
    "Dash_Card_PendingGames": ("{0} 个启用游戏还没有照片", "{0} enabled games have no photos yet"),
    "Dash_Card_AllGamesHavePhotos": ("全部启用游戏均有照片", "All enabled games have photos"),
    "Dash_Card_LastSync": ("最近同步", "Last sync"),
    "Dash_Card_NeverSynced": ("尚未执行过同步", "Never synced"),
    "Dash_Card_AccountStatus": ("账号状态", "Account status"),
    "Dash_Card_TokenOk": ("Token 可正常调用接口", "Token works"),
    "Dash_Card_GoSettings": ("前往设置页完成登录", "Sign in from the Settings page"),
    "Dash_Sync_Fetched": ("拉取 {0} 条 · 已同步 {1} 条", "Fetched {0} · synced {1}"),

    # ---- 通用格式化 ----
    "Format_Unknown": ("未知", "unknown"),
    "Format_HoursMinutes": ("{0} 小时 {1} 分", "{0} h {1} min"),
    "Format_Minutes": ("{0} 分钟", "{0} min"),

    # ---- Gallery ----
    "Gallery_Filter_All": ("全部", "All"),
    "Gallery_Detail_Untitled": ("无标题", "Untitled"),
    "Gallery_Detail_NoDescription": ("无描述", "No description"),
    "Gallery_Detail_Meta": ("{0} · 上传 {1} · 下载 {2}", "{0} · uploaded {1} · downloaded {2}"),

    # ---- Settings：账号与状态 ----
    "Settings_Config_Saved": ("设置已保存", "Settings saved"),
    "Settings_Token_Refreshed": ("Token 已刷新", "Token refreshed"),
    "Settings_Token_NotConfigured": ("未配置", "Not configured"),
    "Settings_Token_Expired": ("已过期", "Expired"),
    "Settings_Token_Valid": ("有效", "Valid"),

    # ---- Settings：检查更新 ----
    "Update_Checking": ("正在检查更新…", "Checking for updates…"),
    "Update_CheckFailed": ("检查更新失败", "Update check failed"),
    "Update_CheckFailedDetail": ("检查更新失败：{0}", "Update check failed: {0}"),
    "Update_FoundNew": ("发现新版本 v{0}（当前 v{1}）", "New version v{0} available (current v{1})"),
    "Update_UpToDate": ("已是最新版本（v{0}）", "Already up to date (v{0})"),

    # ---- Settings：更新流程 ----
    "Update_Preparing": ("正在准备更新 {0}…", "Preparing to update to {0}…"),
    "Update_IncrementalVerified": ("增量更新已校验，正在准备替换…",
                                   "Incremental update verified, preparing to replace…"),
    "Update_SimulateIncremental": ("模拟模式（增量）：{0}", "Simulation (incremental): {0}"),
    "Update_ExitingIncremental": ("即将退出并完成增量更新…",
                                  "Exiting to finish the incremental update…"),
    "Update_DownloadingFull": ("正在下载完整包 {0}… {1:F0}%", "Downloading full package {0}… {1:F0}%"),
    "Update_DownloadingFullUnknown": ("正在下载完整包…", "Downloading full package…"),
    "Update_SimulateBasic": ("模拟模式：下载与哈希已校验；{0}",
                             "Simulation: download and hash verified; {0}"),
    "Update_FullVerified": ("下载完成，哈希校验通过，正在准备替换…",
                            "Download complete and hash verified, preparing to replace…"),
    "Update_Exiting": ("即将退出并完成更新…", "Exiting to finish the update…"),
    "Update_Cancelled": ("已取消下载。", "Download cancelled."),
    "Update_InstalledMode": ("检测到安装版（注册表记录位置：{0}），安装程序将使用该目录",
                             "Installed build detected (recorded location: {0}); the installer will use it"),
    "Update_PortableMode": ("检测到免安装版，安装程序将装回当前目录：{0}",
                            "Portable build detected; the installer will reinstall into {0}"),
    "Update_DownloadingInstaller": ("正在下载安装程序 {0}…", "Downloading installer {0}…"),
    "Update_DownloadingInstallerPct": ("正在下载安装程序 {0}… {1:F0}%",
                                       "Downloading installer {0}… {1:F0}%"),
    "Update_DownloadingInstallerUnknown": ("正在下载安装程序…", "Downloading installer…"),
    "Update_SimulateInstaller": ("模拟模式（安装程序）：下载与哈希已校验；{0}",
                                 "Simulation (installer): download and hash verified; {0}"),
    "Update_InstallerVerified": ("安装程序已校验，即将退出并开始安装…",
                                 "Installer verified; exiting to start installation…"),
    "Update_ExitingForInstall": ("应用即将退出并完成安装…",
                                 "The app will exit to finish installing…"),
    "Update_InstallerUnavailable": ("安装程序方式不可用，改用其他方式…",
                                    "Installer unavailable; trying another method…"),
    "Update_IncrementalUnavailable": ("增量更新不适用，改用完整包…",
                                      "Incremental update not applicable; using the full package…"),
    "Settings_Login_OpeningBrowser": ("正在打开浏览器…", "Opening the browser…"),

    # ---- Sync ----
    "Sync_NoRunningTask": ("当前没有运行中的任务", "No task is running"),
    "Sync_FinishedAt": ("完成时间 {0}", "Finished at {0}"),
    "Sync_LoadingGames": ("正在加载游戏列表…", "Loading the game list…"),
    "Sync_NoSelection": ("未勾选任何游戏，将同步设置页中启用的游戏。",
                         "No games selected; the games enabled in Settings will be synced."),
    "Sync_Selection": ("本次将同步勾选的 {0} 个游戏。", "Will sync the {0} selected games."),
    "Sync_Elapsed": ("已用 {0}", "Elapsed {0}"),
    "Sync_EtaEstimating": ("正在估算剩余时间…", "Estimating time remaining…"),
    "Sync_EtaAlmostDone": ("即将完成", "Almost done"),
    "Sync_EtaRemaining": ("预计剩余 {0}", "{0} remaining"),

    # ---- SettingsPage：手动更新对话框 ----
    "Settings_ManualUpdate_Title": ("需要手动更新", "Manual update required"),
    "Settings_ManualUpdate_Body": ("无法自动更新：{0}\n\n可以在打开的下载页手动下载安装包并运行。",
                                   "Cannot update automatically: {0}\n\nYou can download and run the installer from the releases page."),
    "Settings_ManualUpdate_Open": ("打开发布页", "Open releases page"),
    "Common_Cancel": ("取消", "Cancel"),
}


def load_existing(path: Path) -> dict[str, str]:
    """读已有 resw，保留手工加入的条目（例如 XAML 抽出来的 118 条）。"""
    if not path.exists():
        return {}
    text = path.read_text(encoding="utf-8")
    return {
        m.group(1): m.group(2)
        for m in re.finditer(r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>', text, re.DOTALL)
    }


def build_resw(entries: dict[str, str], note: str) -> str:
    items = "\n".join(
        f'  <data name="{k}" xml:space="preserve">\n    <value>{v}</value>\n  </data>'
        for k, v in entries.items()
    )
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n<root>\n'
        f"  <!--\n    {note}\n    由 tools/i18n_apply.py（XAML 侧）与 tools/i18n_apply_cs.py（C# 侧）共同维护，\n"
        "    手工改动会被脚本覆盖。\n  -->\n"
        f"{items}\n</root>\n"
    )


def main() -> int:
    check_only = "--check" in sys.argv
    zh_path = WEB / "Strings" / "zh-CN" / "Resources.resw"
    en_path = WEB / "Strings" / "en-us" / "Resources.resw"

    zh = load_existing(zh_path)
    en = load_existing(en_path)

    added = 0
    for key, (zh_text, en_text) in STRINGS.items():
        # ★ 资源名必须带属性后缀（和 XAML 侧一致）：C# 的 Get/Format 会按
        #   "<键>.<后缀>" 去取（默认 Text），若 resw 里存的是裸键就永远取不到，
        #   界面上会直接显示键名（实测踩过：Dash_Token_Missing 之类的字符串出现在了界面上）。
        resource = key if "." in key else f"{key}.Text"
        if resource not in zh:
            zh[resource] = zh_text
            added += 1
        en[resource] = en_text
    # 保持稳定顺序：先原有（XAML 的），再新增（C# 的）
    print(f"C# 侧文案: {len(STRINGS)} 条（新增 {added} 条）；resw 现有 {len(zh)} / {len(en)} 条")

    if check_only:
        missing_en = [k for k in zh if k not in en]
        if missing_en:
            print(f"[FAIL] 英文缺 {len(missing_en)} 条: {missing_en[:10]}")
            return 1
        print("[OK] 中英条目对齐")
        return 0

    zh_path.write_text(build_resw(zh, "界面文案（简体中文，默认语言）"), encoding="utf-8", newline="\n")
    en_path.write_text(build_resw(en, "UI strings (English)"), encoding="utf-8", newline="\n")
    print(f"已写 resw: zh-CN {len(zh)} 条、en-us {len(en)} 条")

    # 把 C# 里的字面量替换成 StringLocalizer 调用（按 中文原文 → 键 映射）
    replace_map = {zh_text: key for key, (zh_text, _) in STRINGS.items()}
    total = 0
    for path in sorted(WEB.rglob("*.cs")):
        if any(p in str(path) for p in ("obj", "bin", "dist", "tools", "StringLocalizer.cs")):
            continue
        text = path.read_text(encoding="utf-8")
        original = text
        for zh_text, key in replace_map.items():
            # 只替换整串字面量（含插值串），避免误伤注释与部分匹配
            text = text.replace(f'"{zh_text}"', f'StringLocalizer.Get("{key}")')
        if text != original:
            path.write_text(text, encoding="utf-8", newline="\n")
            n = sum(original.count(f'"{t}"') for t in replace_map)
            print(f"  {path.name}: 替换 {n} 处")
            total += n
    print(f"合计替换 {total} 处")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
