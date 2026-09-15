"""修正 i18n_apply_cs.py 的替换结果：带占位符的条目要用 Format 而不是 Get。

背景：`StringLocalizer.Get` 只返回整串，而 `{0}` 这类占位符需要 `Format` 来填充。
C# 里 `{...}` 与非逐字字符串的插值语法冲突，所以这里按"调用点"改写，
而不是全局字符串替换：
  StringLocalizer.Get("K")            → 若 K 的文案含 {N}，改为 StringLocalizer.Format("K")
  StringLocalizer.Get("K") + suffix   → 保持（由调用方拼接）
"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"
ZH_RESW = WEB / "Strings" / "zh-CN" / "Resources.resw"

CALL = re.compile(r'StringLocalizer\.Get\("([^"]+)"\)')


def main() -> int:
    text = ZH_RESW.read_text(encoding="utf-8")
    values = {
        m.group(1): m.group(2)
        for m in re.finditer(r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>', text, re.DOTALL)
    }
    with_placeholders = {k for k, v in values.items() if re.search(r"\{\d", v)}
    print(f"含占位符的条目: {len(with_placeholders)} 条")

    changed = 0
    for path in sorted(WEB.rglob("*.cs")):
        if any(p in str(path) for p in ("obj", "bin", "dist", "tools", "StringLocalizer.cs")):
            continue
        content = path.read_text(encoding="utf-8")
        original = content

        def fix(m: re.Match[str]) -> str:
            key = m.group(1)
            return f'StringLocalizer.Format("{key}")' if key in with_placeholders else m.group(0)

        content = CALL.sub(fix, content)
        if content != original:
            path.write_text(content, encoding="utf-8", newline="\n")
            n = len(CALL.findall(original)) 
            print(f"  {path.name}: 处理 {n} 处调用")
            changed += 1
    print(f"涉及 {changed} 个文件")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
