"""去掉 <Window> 元素上的 x:Uid，并把窗口标题改由 C# 设置。

为什么要单独处理：
`x:Uid` 的赋值机制对 `Window` 不生效（它不是 FrameworkElement），一旦注入就会在启动时抛
「Failed to assign to property 'Microsoft.UI.Xaml.Window.Title'」——应用直接起不来。
`<Window>` 也不适合放手写文案：窗口标题属于"非 XAML 属性"，交给
MainWindow.xaml.cs 里的 StringLocalizer 更可靠。

用法：E:\\conda\\envs\\FGS\\python.exe tools\\i18n_fix_window.py
"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "web" / "MainWindow.xaml"

# <Window ... x:Uid="X" ...>  →  <Window ...>
WINDOW_UID = re.compile(r'(<Window\b[^>]*?)\s+x:Uid="[^"]*"')


def main() -> int:
    if not TARGET.exists():
        print(f"找不到 {TARGET}")
        return 1

    text = TARGET.read_text(encoding="utf-8")
    new_text, n = WINDOW_UID.subn(r"\1", text, count=1)
    if n:
        TARGET.write_text(new_text, encoding="utf-8", newline="\n")
    first_line = new_text.split("\n")[0].strip()
    print(f"移除 Window 上的 x:Uid: {n} 处")
    print(f"第 1 行现在是: {first_line}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
