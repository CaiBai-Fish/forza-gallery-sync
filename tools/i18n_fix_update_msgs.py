"""把 SettingsViewModel 里 12 条"更新流程"插值串改为 StringLocalizer.Format。

为什么单独处理：这些是 `$"...{变量}..."` 形式的插值串，字面量替换（i18n_apply_cs.py）
只匹配完整字符串字面量，命中不到；而且必须转成带 {N} 占位符的调用才能翻译
（语序不同的语言需要占位符，不能靠字符串拼接）。

用法：E:\\conda\\envs\\FGS\\python.exe tools\\i18n_fix_update_msgs.py
"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "web" / "ViewModels" / "SettingsViewModel.cs"

# (原文, 键, 占位符对应的 C# 实参表达式)
RULES: list[tuple[str, str, list[str]]] = [
    ('$"检查更新失败：{info.Error}"', "Update_CheckFailedDetail", ["info.Error"]),
    ('$"发现新版本 v{info.Latest}（当前 v{info.Current}）"', "Update_FoundNew",
     ["info.Latest", "info.Current"]),
    ('$"已是最新版本（v{info.Current}）"', "Update_UpToDate", ["info.Current"]),
    ('$"正在准备更新 {LatestVersion}…"', "Update_Preparing", ["LatestVersion"]),
    ('$"模拟模式（增量）：{check}"', "Update_SimulateIncremental", ["check"]),
    ('$"正在下载完整包 {LatestVersion}… {DownloadProgress:F0}%"', "Update_DownloadingFull",
     ["LatestVersion", "DownloadProgress"]),
    ('$"模拟模式：下载与哈希已校验；{check}"', "Update_SimulateBasic", ["check"]),
    ('$"检测到安装版（注册表记录位置：{installedLocation}），安装程序将使用该目录"',
     "Update_InstalledMode", ["installedLocation"]),
    ('$"检测到免安装版，安装程序将装回当前目录：{dirOverride}"', "Update_PortableMode",
     ["dirOverride"]),
    ('$"正在下载安装程序 {LatestVersion}…"', "Update_DownloadingInstaller", ["LatestVersion"]),
    ('$"正在下载安装程序 {LatestVersion}… {DownloadProgress:F0}%"',
     "Update_DownloadingInstallerPct", ["LatestVersion", "DownloadProgress"]),
    ('$"模拟模式（安装程序）：下载与哈希已校验；{check}"', "Update_SimulateInstaller", ["check"]),
]


def main() -> int:
    text = TARGET.read_text(encoding="utf-8")
    total = 0
    for original, key, args in RULES:
        if original not in text:
            print(f"  [skip] 未找到: {original[:50]}")
            continue
        args_text = ", ".join(args)
        call = f'StringLocalizer.Format("{key}", {args_text})'
        text = text.replace(original, call)
        total += 1

    TARGET.write_text(text, encoding="utf-8", newline="\n")
    print(f"替换 {total} / {len(RULES)} 条")

    # 确认没有残留（这 12 条的原文不应再出现）
    leftover = [o for o, _, _ in RULES if o in text]
    if leftover:
        print(f"  [warn] 仍残留 {len(leftover)} 条: {leftover[:3]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
