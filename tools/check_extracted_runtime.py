"""验证已解压的 Python 运行时可以真正使用 playwright（登录用）。

用法: python tools/check_extracted_runtime.py <python_home 目录>
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

PROBE = r"""
import json, sys
info = {"python": sys.version.split()[0]}
try:
    import requests
    info["requests"] = requests.__version__
except Exception as exc:
    info["requests_error"] = f"{type(exc).__name__}: {exc}"
try:
    import playwright
    from playwright.sync_api import sync_playwright
    info["playwright"] = getattr(playwright, "__version__", "unknown")
    with sync_playwright() as p:
        info["chromium_executable"] = p.chromium.executable_path
        browser = p.chromium.launch(channel="msedge", headless=True)
        info["browser_version"] = browser.version
        browser.close()
except Exception as exc:
    info["playwright_error"] = f"{type(exc).__name__}: {exc}"
print(json.dumps(info, ensure_ascii=False))
"""


def main() -> int:
    home = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    python_exe = home / "python.exe"
    if not python_exe.exists():
        print(f"[FAIL] 找不到 {python_exe}")
        return 1

    site = home / "Lib" / "site-packages"
    print(f"python:      {python_exe}")
    print(f"site-packages: {site} (存在={site.exists()})")

    env = {"PYTHONPATH": str(site), "PYTHONIOENCODING": "utf-8"}
    import os

    full_env = dict(os.environ)
    full_env.update(env)

    proc = subprocess.run(
        [str(python_exe), "-c", PROBE],
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=full_env,
        timeout=180,
    )
    print(f"退出码: {proc.returncode}")
    if proc.stdout.strip():
        print(f"stdout: {proc.stdout.strip()}")
    if proc.stderr.strip():
        print(f"stderr: {proc.stderr.strip()[:2000]}")

    ok = proc.returncode == 0 and "playwright_error" not in proc.stdout and "browser_version" in proc.stdout
    print("\n[PASS] 解压后的运行时可用 playwright 启动浏览器" if ok else "\n[FAIL] 解压后的运行时无法使用 playwright")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
