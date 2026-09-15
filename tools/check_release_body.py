"""校验 CI 里 "Prepare release body from CHANGELOG" 这一步的实际输出。

把 workflow 中该步骤的 pwsh 脚本抽出来、改掉输出路径后在本地执行，
确认生成的 Release 说明格式正确（换行、列表、章节齐全）。

用法: python tools/check_release_body.py 1.0.5
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

WORKFLOW = Path(".github/workflows/build-release.yml")


def main() -> int:
    version = sys.argv[1] if len(sys.argv) > 1 else "1.0.5"
    text = WORKFLOW.read_text(encoding="utf-8")

    # 找到 "Prepare release body from CHANGELOG" 步骤的 run: | 块
    marker = "Prepare release body from CHANGELOG"
    index = text.find(marker)
    if index < 0:
        print("[FAIL] 工作流里找不到该步骤")
        return 1

    after = text[index:]
    run_at = after.find("run: |")
    if run_at < 0:
        print("[FAIL] 该步骤没有 run: | 块")
        return 1

    body_lines: list[str] = []
    for line in after[run_at:].splitlines()[1:]:
        if line.strip() == "":
            body_lines.append("")
            continue
        if not line.startswith(" " * 10):  # run 块缩进
            break
        body_lines.append(line[10:])
    script = "\n".join(body_lines)
    if "RELEASE_BODY_PATH" not in script:
        print("[FAIL] 抽出的脚本不像该步骤")
        return 1

    out = Path(tempfile.gettempdir()) / f"fgs-release-body-check-{version}.md"
    script = script.replace("release-body.md", out.name)

    with tempfile.TemporaryDirectory() as tmp:
        script_path = Path(tmp) / "gen.ps1"
        script_path.write_text(script, encoding="utf-8-sig")  # pwsh 5.1 需要 BOM
        env_path = Path(tmp) / "github-env.txt"
        env_path.touch()
        env = dict(
            os.environ,
            VERSION=version,
            RUNNER_TEMP=tempfile.gettempdir(),
            GITHUB_ENV=str(env_path),
        )
        proc = subprocess.run(
            ["powershell", "-ExecutionPolicy", "Bypass", "-File", str(script_path)],
            capture_output=True, text=True, encoding="utf-8", errors="replace", env=env,
        )
        print(f"退出码: {proc.returncode}")
        if proc.stdout.strip():
            print(proc.stdout.strip())
        if proc.stderr.strip():
            print(f"stderr: {proc.stderr.strip()[:600]}")

    if not out.exists():
        print(f"[FAIL] 没有生成 {out}")
        return 1

    body = out.read_text(encoding="utf-8").lstrip("\ufeff")
    lines = body.splitlines()
    print(f"\n=== 生成结果（{len(body)} 字符 / {len(lines)} 行）===")
    for line in lines[:10]:
        print(f"  | {line}")

    problems = []
    if not body.startswith(f"## Forza Gallery Sync {version}"):
        problems.append("标题不对")
    # 资产清单必须包含安装程序（1.0.4 之前这里漏了 setup.exe）
    if "setup.exe" not in body:
        problems.append("资产清单缺少安装程序")
    # 关键：不能出现"整个 body 挤在一行"的情况
    if len(body) > 200 and len(lines) < 10:
        problems.append(f"换行丢失（{len(body)} 字符只有 {len(lines)} 行）")
    if "### " not in body:
        problems.append("没有包含 CHANGELOG 章节")
    if "\\n" in body:
        problems.append("出现字面 \\n 转义")

    out.unlink(missing_ok=True)

    if problems:
        print(f"\n[FAIL] {len(problems)} 个问题:")
        for item in problems:
            print(f"  - {item}")
        return 1

    print(f"\n[PASS] 发布说明生成正确（{len(lines)} 行，含安装程序条目与 CHANGELOG 章节）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
