"""把 XAML 文案抽取结果落成 .resw 并注入 x:Uid（一次性改造脚本）。

用法：E:\\conda\\envs\\FGS\\python.exe tools\\i18n_apply.py [--check]

为什么用脚本而不是手工改 118 处：
  - 键名必须跨语言严格一致，手工很容易漏；脚本从同一份映射生成两套资源，天然对齐；
  - 注入规则要处理 x:Bind / ToolTip 附加属性等特殊情况，用规则表达比人肉判断可靠。
"""
from __future__ import annotations

import io
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"
MAP_FILE = WEB / "i18n-map.json"

# 英文译文。键来自映射文件；缺译的会在 --check 里报出来。
EN = {
    "MainWindow_Title_00": "Forza Gallery Sync Console",
    "MainWindow_Content_00": "Library",
    "MainWindow_Content_01": "Overview",
    "MainWindow_Content_02": "Photos",
    "MainWindow_Content_03": "Tasks",
    "MainWindow_Content_04": "Sync",
    "MainWindow_Content_05": "Settings",
    "MainWindow_Content_06": "Not signed in",
    "MainWindow_Tooltip_00": "Open Settings to configure the token",

    "DashboardPage_Text_00": "Overview",
    "DashboardPage_Text_01": "Statistics for synced photos. Sync options and account settings live in the Sync and Settings pages.",
    "DashboardPage_Tooltip_00": "Reload statistics",
    "DashboardPage_Text_02": "Refresh",
    "DashboardPage_Text_03": "Go to Sync",
    "DashboardPage_Title_00": "Failed to load statistics",
    "DashboardPage_Title_01": "Account not ready",
    "DashboardPage_Content_00": "Go to Settings",
    "DashboardPage_Text_04": "Total photos",
    "DashboardPage_Text_05": "Last sync",
    "DashboardPage_Text_06": "Browse library",
    "DashboardPage_Text_07": "By game",
    "DashboardPage_Text_08": "No photos yet — run a sync first",
    "DashboardPage_Text_09": "Last sync",
    "DashboardPage_Text_10": "No sync history yet",
    "DashboardPage_Text_11": "Latest photos",
    "DashboardPage_Content_01": "View all",
    "DashboardPage_Text_12": "Copy image",
    "DashboardPage_Text_13": "Open with default app",
    "DashboardPage_Text_14": "Open containing folder",
    "DashboardPage_Text_15": "Untitled",
    "DashboardPage_Text_16": "Latest photos will appear here after a sync",
    "DashboardPage_Tooltip_01": "Close preview",
    "DashboardPage_Text_17": "Copy image",
    "DashboardPage_Text_18": "Open with default app",
    "DashboardPage_Text_19": "Open containing folder",
    "DashboardPage_Text_20": "Copy image",
    "DashboardPage_Text_21": "Open with default app",

    "GalleryPage_Tooltip_00": "Back to library",
    "GalleryPage_PlaceholderText_00": "Search title / ID / game",
    "GalleryPage_PlaceholderText_01": "All games",
    "GalleryPage_PlaceholderText_02": "All months",
    "GalleryPage_Tooltip_01": "Refresh",
    "GalleryPage_Tooltip_02": "Open the download folder in File Explorer",
    "GalleryPage_Text_00": "Untitled",
    "GalleryPage_Text_01": "View details",
    "GalleryPage_Text_02": "Copy image",
    "GalleryPage_Text_03": "Open with default app",
    "GalleryPage_Text_04": "Open containing folder",
    "GalleryPage_Text_05": "No photos found",
    "GalleryPage_Text_06": "Try different filters, or go to the Sync page to fetch photos.",
    "GalleryPage_Tooltip_03": "Previous page",
    "GalleryPage_Tooltip_04": "Next page",
    "GalleryPage_Text_07": "Copy image",
    "GalleryPage_Text_08": "Open with default app",
    "GalleryPage_Text_09": "Open containing folder",
    "GalleryPage_Tooltip_05": "Show / hide photo details",
    "GalleryPage_Text_10": "Uploaded",
    "GalleryPage_Text_11": "Downloaded",
    "GalleryPage_Text_12": "Photo ID",
    "GalleryPage_Text_13": "Local path",
    "GalleryPage_Text_14": "Description",
    "GalleryPage_Text_15": "Copy image",
    "GalleryPage_Text_16": "Open with default app",

    "SettingsPage_Text_00": "Settings",
    "SettingsPage_Text_01": "Global configuration for account, storage, network and concurrency.",
    "SettingsPage_Text_02": "Account and token",
    "SettingsPage_Text_03": "Sign in with browser",
    "SettingsPage_Text_04": "Refresh token",
    "SettingsPage_Text_05": "Sign in to your Xbox / Microsoft account in the popup window (two-step verification supported). The token is saved automatically.",
    "SettingsPage_Text_06": "Storage",
    "SettingsPage_Text_07": "Download folder",
    "SettingsPage_Text_08": "Choose…",
    "SettingsPage_Text_09": "Database",
    "SettingsPage_Text_10": "Config file",
    "SettingsPage_Text_11": "Network and concurrency",
    "SettingsPage_Text_12": "Page size",
    "SettingsPage_Text_13": "Pagination mode",
    "SettingsPage_Text_14": "Concurrent threads",
    "SettingsPage_Text_15": "Retry attempts",
    "SettingsPage_Text_16": "Timeout (seconds)",
    "SettingsPage_Header_00": "Verify SSL certificates",
    "SettingsPage_OnContent_00": "Verify normally",
    "SettingsPage_OffContent_00": "Disabled (bypass for self-signed certificates)",
    "SettingsPage_Text_17": "Enabled games",
    "SettingsPage_Text_18": "This is the default sync scope; the Sync page can override it per run.",
    "SettingsPage_Text_19": "Save",
    "SettingsPage_Text_20": "Reload",
    "SettingsPage_Text_21": "About and updates",
    "SettingsPage_Text_22": "Current version",
    "SettingsPage_Text_23": "Check for updates",
    "SettingsPage_Text_24": "Download and install",
    "SettingsPage_Header_01": "Fall back to incremental update on failure",
    "SettingsPage_OnContent_01": "Only download changed files",
    "SettingsPage_OffContent_01": "Download the full package",
    "SettingsPage_Text_25": "Download and install fetches the official installer, verifies its SHA256, then exits and installs silently before restarting. A portable copy is reinstalled in place; an installed copy updates its original folder. If the installer is unavailable it falls back to incremental or full-package update.",
    "SettingsPage_Content_00": "Open releases page",

    "SyncPage_Text_00": "Sync",
    "SyncPage_Text_01": "Pick the games for this run and start. Global options such as download folder and concurrency are in Settings.",
    "SyncPage_Text_02": "Games for this run",
    "SyncPage_Text_03": "Maximum photos",
    "SyncPage_PlaceholderText_00": "Leave empty for no limit",
    "SyncPage_Text_04": "Start with a small number to verify your configuration.",
    "SyncPage_Header_00": "Force re-download",
    "SyncPage_OnContent_00": "Overwrite files that already exist locally",
    "SyncPage_OffContent_00": "Skip photos that were already downloaded",
    "SyncPage_Text_05": "Start sync",
    "SyncPage_Text_06": "Stop",
    "SyncPage_Title_00": "Operation failed",
    "SyncPage_Text_07": "Sync progress",
    "SyncPage_Text_08": "Added",
    "SyncPage_Text_09": "Skipped",
    "SyncPage_Text_10": "Failed",
    "SyncPage_Text_11": "Sync progress",
    "SyncPage_Text_12": "Added",
    "SyncPage_Text_13": "Skipped",
    "SyncPage_Text_14": "Failed",
    "SyncPage_Title_01": "Cancelling",
    "SyncPage_Text_15": "Failure details",
}

RESW_TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <!--
    生成的界面文案文件（由 tools/i18n_apply.py 从 web/i18n-map.json 生成）。

    ⚠️ 资源名必须带**属性后缀**：x:Uid="K" 时 WinUI 会去找 "K.Text" / "K.Content" /
    "K.PlaceholderText" …（后缀就是它在 XAML 里要设的那个属性名）。
    早期版本生成的是裸键 "K"，WinUI 找不到对应属性 → **所有 x:Uid 文案渲染成空白**
    （截图一看就是导航栏和页面标题全没了），这是本项目踩过的最隐蔽的一次：
    UI Automation 检查只会显示"文本为空"而不报错，所以必须实际截图确认。

    后缀按原属性映射：<模块>_<属性>_<序号> 里的中段就是属性名，转小写后作后缀即可
    （Tooltip / Header / OnContent / OffContent / PlaceholderText 都直接对应属性名）。
  -->
{items}
</root>
"""

# 键名中段（属性）→ x:Uid 需要的属性后缀
ATTR_SUFFIX = {
    "Text": "Text",
    "Content": "Content",
    "Header": "Header",
    "Title": "Title",
    "PlaceholderText": "PlaceholderText",
    "OnContent": "OnContent",
    "OffContent": "OffContent",
    "Tooltip": "ToolTipService.ToolTip",
}


def key_to_resource_name(key: str) -> str:
    """MainWindow_Content_01 → MainWindow_Content_01.Content（供 x:Uid 使用）。

    裸键是给 C# 的 StringLocalizer 用的；XAML 的 x:Uid 必须带属性后缀。
    """
    parts = key.rsplit("_", 2)          # [模块, 属性, 序号]
    attr = parts[1] if len(parts) == 3 else "Text"
    suffix = ATTR_SUFFIX.get(attr, "Text")
    return f"{key}.{suffix}"


def esc(text: str) -> str:
    return (text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


def build_resw(pairs: list[tuple[str, str]], note: str) -> str:
    """只生成**带属性后缀**的资源名（K.Text / K.Content / …）。

    为什么不是"裸键 + 带后缀"两份：PRI 会把点号当路径分隔符，
    `K` 与 `K.Text` 同时存在时 K 既是资源又是作用域 → 编译直接失败
    （PRI175 / PRI278「实体同时被定义为资源和范围，这是不允许的」，实测踩过）。

    所以统一用带后缀的名字：XAML 的 x:Uid="K" 天然找 K.<属性>；
    C# 侧也用同一个名字取（见 StringLocalizer.GetForUi，会把键转成 K.Text）。
    """
    items = "\n".join(
        f'  <data name="{key_to_resource_name(k)}" xml:space="preserve">\n    <value>{esc(v)}</value>\n  </data>'
        for k, v in pairs
    )
    return RESW_TEMPLATE.format(items=items)


def main() -> int:
    check_only = "--check" in sys.argv
    mapping = json.loads(MAP_FILE.read_text(encoding="utf-8"))

    missing_en = [e["key"] for e in mapping if e["key"] not in EN]
    if missing_en:
        print(f"[FAIL] 英文缺译 {len(missing_en)} 条：")
        for k in missing_en[:20]:
            print("   ", k)
        return 1

    zh_pairs = [(e["key"], e["value"]) for e in mapping]
    en_pairs = [(e["key"], EN[e["key"]]) for e in mapping]

    if check_only:
        print(f"[OK] 英文译文齐全（{len(en_pairs)} 条）")
        return 0

    (WEB / "Strings" / "zh-CN").mkdir(parents=True, exist_ok=True)
    (WEB / "Strings" / "en-us").mkdir(parents=True, exist_ok=True)
    (WEB / "Strings" / "zh-CN" / "Resources.resw").write_text(
        build_resw(zh_pairs, "简体中文（默认语言）"), encoding="utf-8", newline="\n")
    (WEB / "Strings" / "en-us" / "Resources.resw").write_text(
        build_resw(en_pairs, "English"), encoding="utf-8", newline="\n")
    print(f"已写 ru-sw：zh-CN {len(zh_pairs)} 条、en-us {len(en_pairs)} 条")

    # ---- 注入 x:Uid ----
    # 规则（踩过的坑都写在这）：
    #   * 一个控件只能有一个 x:Uid：早期版本对同一标签里的多个中文属性各注入一个，
    #     直接编译失败（WMC9997“x:Uid 是重复的特性名称”）。所以**每个标签最多注入一个**。
    #   * x:Uid 只能驱动一个属性（<key>.Text / <key>.Content 等），标签里其余中文属性
    #     无法靠它本地化，统一交给 C# 里的 StringLocalizer 赋值 → 收集到 pending 列表。
    #   * ToolTipService.ToolTip 是附加属性，一律走 C#。
    #   * Text="{x:Bind ...}" 不能替换，只把 FallbackValue 里的中文交给 C#。
    tag_re = re.compile(r"<[A-Za-z][A-Za-z0-9_.:]*\b[^>]*?/?>", re.DOTALL)
    attr_re = re.compile(
        r'\b(Text|Content|Header|Title|PlaceholderText|OnContent|OffContent|'
        r'ToolTipService\.ToolTip)\s*=\s*"([^"]*)"')
    stats = {"injected": 0, "pending": 0}
    pending: list[dict] = []

    by_file: dict[str, list[dict]] = {}
    for e in mapping:
        by_file.setdefault(e["file"], []).append(e)

    for rel, items in by_file.items():
        path = ROOT / rel
        text = path.read_text(encoding="utf-8")
        wanted = {e["value"]: e["key"] for e in items}     # 文案 → 键（同文案同键）

        chunks: list[tuple[int, int, str]] = []            # (start, end, replacement)
        for m in tag_re.finditer(text):
            tag = m.group(0)

            # <Window> 不能注入 x:Uid：Window 不是 FrameworkElement，x:Uid 的赋值机制
            # 对它不生效，启动时直接抛
            # 「Failed to assign to property 'Microsoft.UI.Xaml.Window.Title'」→ 应用起不来。
            # 窗口标题改由 MainWindow.xaml.cs 用 StringLocalizer 设置。
            if re.match(r"<\s*Window\b", tag):
                continue

            # ★ 幂等保护：已经注入过 x:Uid 的标签直接跳过。
            #   否则重复运行本脚本会在同一标签里叠加第二个 x:Uid，直接编译失败
            #   （WMC9997「x:Uid 是重复的特性名称」——本项目反复踩过，因为脚本需要多次运行）。
            if re.search(r'\sx:Uid="', tag):
                continue

            attrs = [am for am in attr_re.finditer(tag) if re.search(r"[\u4e00-\u9fff]", am.group(2))]
            if not attrs:
                continue

            # 先决定哪些属性留在 XAML 里、哪个用它做 x:Uid
            kept_in_csharp = []
            uid_attr = None
            for am in attrs:
                name, value = am.group(1), am.group(2)
                if name == "ToolTipService.ToolTip" or "x:Bind" in value:
                    kept_in_csharp.append((name, value))
                    continue
                if uid_attr is None and value in wanted:
                    uid_attr = (name, value, wanted[value])
                else:
                    kept_in_csharp.append((name, value))

            for name, value in kept_in_csharp:
                pending.append({"file": rel, "attr": name, "value": value, "key": wanted.get(value, "")})

            if uid_attr is None:
                continue

            name, value, key = uid_attr
            # 把用作 x:Uid 的那个属性删掉（x:Uid 会负责它），再在元素名后插入 x:Uid
            new_tag = re.sub(r'\s*\b' + re.escape(name) + r'\s*=\s*"' + re.escape(value) + r'"', "",
                             tag, count=1)
            mm = re.match(r"<([A-Za-z][A-Za-z0-9_.:]*)", new_tag)
            new_tag = f'<{mm.group(1)} x:Uid="{key}"' + new_tag[mm.end():]

            chunks.append((m.start(), m.end(), new_tag))
            stats["injected"] += 1

        for start, end, replacement in sorted(chunks, reverse=True):
            text = text[:start] + replacement + text[end:]
        path.write_text(text, encoding="utf-8", newline="\n")

    (WEB / "i18n-pending.json").write_text(
        json.dumps(pending, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")
    stats["pending"] = len(pending)
    print(f"x:Uid 注入 {stats['injected']} 个标签；"
          f"其余 {stats['pending']} 处交给 C#（已写入 web/i18n-pending.json）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
