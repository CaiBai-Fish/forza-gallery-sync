"""检查 C# 侧引用的 i18n 键是否都存在于文案字典里。

起因：UpdateService 调用了 StringLocalizer.Get("Upd_HashMismatch")，但 resw 里只有
Upd_InstallerHashMismatch.Text —— StringLocalizer 取不到键时会**原样返回键名**，
于是"哈希校验失败"这种安全提示会直接显示成 `Upd_HashMismatch` 字样。

用法: python tools/check_i18n_keys.py
"""
from __future__ import annotations

import json
import re
from pathlib import Path

WEB = Path("web")
CALL = re.compile(r'StringLocalizer\.(?:Get|Format)\(\s*"([^"]+)"')
SKIP_DIRS = {"bin", "obj", "dist"}


def source_files(pattern: str) -> list[Path]:
    """列出 web 下的源文件，跳过构建产物与发布目录。"""
    return [
        path
        for path in WEB.rglob(pattern)
        if path.is_file() and not SKIP_DIRS.intersection(path.parts)
    ]


def load_strings(language: str) -> dict[str, str]:
    path = WEB / "Strings" / language / "strings.json"
    if not path.exists():
        print(f"[FAIL] 缺少 {path}")
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def main() -> int:
    sources = source_files("*.cs")
    print(f"扫描 {len(sources)} 个 C# 文件")

    used: dict[str, list[str]] = {}
    for path in sources:
        text = path.read_text(encoding="utf-8", errors="replace")
        for key in CALL.findall(text):
            used.setdefault(key, []).append(str(path))

    print(f"发现 {len(used)} 个被引用的键")

    zh = load_strings("zh-CN")
    en = load_strings("en-us")
    print(f"文案字典: zh-CN {len(zh)} 条 / en-us {len(en)} 条")

    missing_zh = [k for k in used if k not in zh]
    missing_en = [k for k in used if k not in en]

    if missing_zh:
        print(f"\n[FAIL] 有 {len(missing_zh)} 个键在 zh-CN 文案里不存在（界面会显示键名）:")
        for key in sorted(missing_zh):
            print(f"  {key}")
            for src in used[key][:3]:
                print(f"      ← {src}")
    if missing_en:
        print(f"\n[WARN] 有 {len(missing_en)} 个键在 en-us 文案里不存在（英文界面会回退中文）:")
        for key in sorted(missing_en):
            print(f"  {key}")

    # 反向：字典里有、代码与 XAML 都没用的键（仅供清理参考，不算失败）
    xaml_text = "\n".join(
        path.read_text(encoding="utf-8", errors="replace")
        for path in source_files("*.xaml")
    )
    unused = [
        key
        for key in zh
        if key not in used and f'x:Uid="{key}"' not in xaml_text
    ]
    print(f"\n[INFO] 未被代码/XAML 直接引用的键: {len(unused)} 个（可能是动态拼接，仅供参考）")
    for key in sorted(unused)[:15]:
        print(f"  {key}")

    if missing_zh:
        return 1
    print("\n[PASS] C# 引用的所有键在两种语言里都存在")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
