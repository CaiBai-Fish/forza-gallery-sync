"""从 hashes 分支取清单，与 GitHub Release 上实际下载的文件逐字节核对 SHA-256。

这是"自动更新会不会拒绝安装"的决定性验证：客户端拿到清单后比对的正是这些值。

用法: python tools/verify_release_hashes.py 1.0.4
"""
from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import urllib.request
from pathlib import Path

REPO = "CaiBai-Fish/forza-gallery-sync"


def fetch(url: str, *, binary: bool = False):
    req = urllib.request.Request(url, headers={"User-Agent": "fgs-verify"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        data = resp.read()
    return data if binary else data.decode("utf-8")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_manifest(text: str) -> dict[str, str]:
    entries = {}
    for line in text.splitlines():
        line = line.strip()
        if not line:
            continue
        # 格式: <sha256>  <文件名>（两个空格分隔）
        parts = line.split("  ", 1)
        if len(parts) == 2:
            entries[parts[1].strip()] = parts[0].strip().lower()
    return entries


def main() -> int:
    version = sys.argv[1] if len(sys.argv) > 1 else "1.0.4"

    # 1) hashes 分支的 <版本>.txt
    raw = f"https://raw.githubusercontent.com/{REPO}/hashes/{version}.txt"
    print(f"清单: {raw}")
    try:
        manifest = parse_manifest(fetch(raw))
    except Exception as exc:
        print(f"[FAIL] 取清单失败: {type(exc).__name__}: {exc}")
        return 1

    print(f"清单条目: {len(manifest)}")
    for name, value in manifest.items():
        print(f"  {value}  {name}")

    # 2) 逐项与 Release 上的文件核对
    release_base = f"https://github.com/{REPO}/releases/download/v{version}"
    tmp = Path(tempfile.mkdtemp(prefix="fgs-hash-"))
    failures = []

    for name, expected in manifest.items():
        url = f"{release_base}/{name}"
        target = tmp / name
        print(f"\n下载并校验 {name} ...")
        try:
            target.write_bytes(fetch(url, binary=True))
        except Exception as exc:
            print(f"  [FAIL] 下载失败: {type(exc).__name__}: {exc}")
            failures.append(f"{name}: 下载失败 {exc}")
            continue

        actual = sha256_file(target)
        size_mb = target.stat().st_size / 1024 / 1024
        if actual == expected:
            print(f"  [OK] SHA256 一致（{size_mb:.1f} MB）")
        else:
            print(f"  [FAIL] 不一致（{size_mb:.1f} MB）")
            print(f"         期望 {expected}")
            print(f"         实际 {actual}")
            failures.append(f"{name}: 哈希不一致")
        target.unlink(missing_ok=True)

    # 3) 增量清单也要能取到（自动更新的回退策略依赖它）
    inc_url = f"https://raw.githubusercontent.com/{REPO}/hashes/hashes.json"
    try:
        payload = json.loads(fetch(inc_url))
        print(f"\nhashes.json: version={payload.get('version')}  artifacts={len(payload.get('artifacts', []))}")
    except Exception as exc:
        print(f"\n[WARN] hashes.json 读取失败（客户端会回退到 <版本>.txt）: {exc}")

    tmp.rmdir()

    if failures:
        print(f"\n[FAIL] {len(failures)} 项不通过:")
        for item in failures:
            print(f"  - {item}")
        return 1

    print(f"\n[PASS] 清单 {len(manifest)} 项与 Release 上的实际文件完全一致")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
