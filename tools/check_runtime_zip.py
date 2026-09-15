"""校验 python-runtime.zip 是否包含登录所需的全部依赖。

用法: python tools/check_runtime_zip.py web/python-runtime.zip
"""
from __future__ import annotations

import sys
import zipfile
from pathlib import Path

REQUIRED = [
    "forza_sync/__init__.py",
    "Lib/site-packages/requests/__init__.py",
    "Lib/site-packages/playwright/__init__.py",
    "Lib/site-packages/greenlet/__init__.py",
    "Lib/site-packages/pyee/__init__.py",
    "Lib/site-packages/playwright/driver/node.exe",
    "python313.dll",
]


def main() -> int:
    path = Path(sys.argv[1] if len(sys.argv) > 1 else "web/python-runtime.zip")
    if not path.exists():
        print(f"[FAIL] 找不到 {path}")
        return 1

    print(f"文件: {path}  ({path.stat().st_size / 1024 / 1024:.1f} MB)")

    with zipfile.ZipFile(path) as zf:
        names = [n.replace("\\", "/") for n in zf.namelist()]
        name_set = set(names)

        print(f"条目数: {len(names)}")

        tops: dict[str, int] = {}
        for name in names:
            head = name.split("/", 1)[0]
            tops[head] = tops.get(head, 0) + 1
        print("顶层:")
        for key in sorted(tops):
            print(f"  {key:<16} {tops[key]}")

        playwright = [n for n in names if n.startswith("Lib/site-packages/playwright/")]
        print(f"playwright 条目: {len(playwright)}")
        print(f"driver 条目:     {len([n for n in names if n.startswith('Lib/site-packages/playwright/driver/')])}")

        failed = []
        for required in REQUIRED:
            ok = required in name_set
            print(f"  [{'OK ' if ok else 'FAIL'}] {required}")
            if not ok:
                failed.append(required)

        if playwright:
            sample = sorted(n for n in playwright if "/_impl/" in n)[:5]
            for item in sample:
                print(f"        样例 {item}")

    if failed:
        print(f"\n[FAIL] 缺失 {len(failed)} 项: {failed}")
        return 1
    print("\n[PASS] 运行时包含登录所需依赖")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
