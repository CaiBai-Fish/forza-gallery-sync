"""处理 XAML 里无法用常规 x:Uid 注入的 ToolTip 文案。

背景：`ToolTipService.ToolTip` 是**附加属性**，早期版本的注入脚本把它整类丢给 C#，
结果 8 处提示文案漏译。其实 x:Uid 也支持附加属性，资源名写成
`<键>.ToolTipService.ToolTip` 即可（WinUI 的命名约定）。

做法：把这些控件上的 `ToolTipService.ToolTip="中文"` 换成 `x:Uid="键"`，
并在两个 resw 里补上 `<键>.ToolTipService.ToolTip`。

用法：E:\\conda\\envs\\FGS\\python.exe tools\\i18n_fix_tooltips.py
"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"

# 文案 → (键名, 英文)
TOOLTIPS: dict[str, tuple[str, str]] = {
    "重新加载统计": ("DashboardPage_TooltipReload", "Reload statistics"),
    "关闭预览": ("DashboardPage_TooltipClosePreview", "Close preview"),
    "返回照片库": ("GalleryPage_TooltipBack", "Back to library"),
    "刷新": ("GalleryPage_TooltipRefresh", "Refresh"),
    "在资源管理器中打开下载目录": ("GalleryPage_TooltipOpenFolder", "Open the download folder in File Explorer"),
    "上一页": ("GalleryPage_TooltipPrevPage", "Previous page"),
    "下一页": ("GalleryPage_TooltipNextPage", "Next page"),
    "显示 / 收起照片信息": ("GalleryPage_TooltipToggleInfo", "Show / hide photo details"),
}

ATTR = re.compile(r'ToolTipService\.ToolTip="([^"]*)"')
TAG_START = re.compile(r'<([A-Za-z][A-Za-z0-9_.:]*)')


def main() -> int:
    # ---- 1) XAML：ToolTipService.ToolTip="中文" → x:Uid="键" ----
    total = 0
    for path in [*(WEB / "Views").glob("*.xaml")]:
        text = path.read_text(encoding="utf-8")
        lines = text.split("\n")
        changed = 0
        for i, line in enumerate(lines):
            m = ATTR.search(line)
            if not m:
                continue
            value = m.group(1)
            if value not in TOOLTIPS:
                continue
            key = TOOLTIPS[value][0]

            # 在最近的上级标签上插入 x:Uid（通常就是本行或上面几行）
            inserted = False
            for j in range(i, max(-1, i - 8), -1):
                if TAG_START.search(lines[j]):
                    if 'x:Uid="' not in lines[j]:
                        lines[j] = TAG_START.sub(lambda mm: f'<{mm.group(1)} x:Uid="{key}"', lines[j], count=1)
                    inserted = True
                    break
            if not inserted:
                print(f"  [warn] 未找到标签: {path.name}:{i + 1}")
                continue

            # 去掉附加属性（x:Uid 会提供它）
            lines[i] = ATTR.sub("", lines[i], count=1)
            changed += 1

        if changed:
            path.write_text("\n".join(lines), encoding="utf-8", newline="\n")
        print(f"  {path.name}: 处理 {changed} 处 ToolTip")
        total += changed

    # ---- 2) resw：补 <键>.ToolTipService.ToolTip ----
    for lang, idx in (("zh-CN", 0), ("en-us", 1)):
        resw = WEB / "Strings" / lang / "Resources.resw"
        text = resw.read_text(encoding="utf-8")
        added = 0
        for zh, (key, en) in TOOLTIPS.items():
            name = f"{key}.ToolTipService.ToolTip"
            if f'name="{name}"' in text:
                continue
            value = zh if idx == 0 else en
            entry = (f'  <data name="{name}" xml:space="preserve">\n'
                     f'    <value>{value}</value>\n  </data>\n')
            text = text.replace("</root>", entry + "</root>", 1)
            added += 1
        resw.write_text(text, encoding="utf-8", newline="\n")
        print(f"  {lang}: 补 {added} 条 ToolTip 资源")

    print(f"合计处理 {total} 处 ToolTip")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
