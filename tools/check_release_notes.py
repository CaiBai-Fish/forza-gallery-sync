"""读取 GitHub Release 的说明并检查格式是否正常。

PowerShell 用 `$body = gh release view ...` 捕获输出会丢换行（这正是本项目把
Release 说明搞坏过一次的原因），所以这里走 REST API 并用 Python 解析。

用法: python tools/check_release_notes.py 1.0.5
"""
from __future__ import annotations

import json
import subprocess
import sys

EXPECTED_MARKERS = [
    ("## Forza Gallery Sync ", "标题"),
    ("setup.exe", "安装程序条目"),
    ("win-x64.zip", "免安装版条目"),
    ("forza-sync.exe", "CLI 条目"),
    ("### ", "CHANGELOG 章节"),
]


def main() -> int:
    version = sys.argv[1] if len(sys.argv) > 1 else "1.0.5"
    proc = subprocess.run(
        ["gh", "api", f"repos/CaiBai-Fish/forza-gallery-sync/releases/tags/v{version}"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if proc.returncode != 0:
        print(f"[FAIL] gh api 失败: {proc.stderr.strip()[:300]}")
        return 1

    payload = json.loads(proc.stdout)
    body = payload.get("body") or ""
    lines = body.splitlines()

    print(f"Release: {payload.get('name')}  (tag {payload.get('tag_name')})")
    print(f"说明长度: {len(body)} 字符 / {len(lines)} 行")
    print("--- 前 12 行 ---")
    for line in lines[:12]:
        print(f"  | {line}")

    problems = []
    for marker, label in EXPECTED_MARKERS:
        if marker not in body:
            problems.append(f"缺少{label}（{marker!r}）")
    if len(body) > 300 and len(lines) < 12:
        problems.append(f"换行丢失：{len(body)} 字符只有 {len(lines)} 行")
    if "\\n" in body:
        problems.append("正文里出现字面 \\n（换行被转义成了两个字符）")

    assets = payload.get("assets", [])
    names = [asset["name"] for asset in assets]
    print(f"\n资产: {len(assets)} 个（非增量 {len([n for n in names if not n.startswith('ForzaGallerySync-inc-')])} 个）")
    for name in names:
        if not name.startswith("ForzaGallerySync-inc-"):
            size = next(a["size"] for a in assets if a["name"] == name)
            print(f"  {name}  {size / 1024 / 1024:.1f} MB")

    if not any(name.endswith("-setup.exe") for name in names):
        problems.append("Release 里没有安装程序资产")

    if problems:
        print(f"\n[FAIL] {len(problems)} 个问题:")
        for item in problems:
            print(f"  - {item}")
        return 1

    print(f"\n[PASS] v{version} 的 Release 说明与资产都正常")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
