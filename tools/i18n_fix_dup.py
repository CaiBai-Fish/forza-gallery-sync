"""修复同一标签里重复的 x:Uid（一次性补丁，也可重复执行）。

背景：早期版本的注入脚本对"同一标签的多个中文属性"各插了一个 x:Uid
（典型是 ToggleSwitch 的 Header + OnContent/OffContent），XAML 编译直接失败：
WMC9997「x:Uid 是重复的特性名称」。

处理策略：同一标签里只保留一个 x:Uid —— 保留**第一个**，其余属性留给 C# 赋值
（OnContent/OffContent 这类次要文案由 StringLocalizer 设置）。
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FILES = [ROOT / "web" / "MainWindow.xaml"] + sorted((ROOT / "web" / "Views").glob("*.xaml"))

UID = re.compile(r'\s*x:Uid="([^"]*)"')


def fix_line(line: str) -> tuple[str, int]:
    hits = list(UID.finditer(line))
    if len(hits) <= 1:
        return line, 0
    for hit in reversed(hits[1:]):          # 保留第一个
        line = line[: hit.start()] + line[hit.end():]
    return line, len(hits) - 1


def main() -> int:
    total = 0
    for path in FILES:
        if not path.exists():
            continue
        text = path.read_text(encoding="utf-8")
        lines = text.split("\n")
        fixed = 0
        for i, line in enumerate(lines):
            new_line, n = fix_line(line)
            if n:
                lines[i] = new_line
                fixed += n
        if fixed:
            path.write_text("\n".join(lines), encoding="utf-8", newline="\n")
        remaining = sum(line.count("x:Uid=") for line in lines)
        print(f"  {path.name}: 修复 {fixed} 处；现有 x:Uid {remaining} 个")
        total += fixed
    print(f"合计修复 {total} 处")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
