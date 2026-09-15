"""精确诊断 Release 正文里是否含"字面反斜杠n"（而非真正的换行）。

用法: python tools/diag_release_body.py 1.0.4
"""
from __future__ import annotations

import json
import subprocess
import sys

BACKSLASH = chr(92)
LETTER_N = chr(110)
NEWLINE = chr(10)
CARRIAGE = chr(13)


def fetch(tag: str) -> str:
    out = subprocess.run(
        ["gh", "api", f"repos/CaiBai-Fish/forza-gallery-sync/releases/tags/{tag}"],
        capture_output=True, text=True, encoding="utf-8",
    ).stdout
    return json.loads(out)["body"]


def main() -> int:
    tag = sys.argv[1] if len(sys.argv) > 1 else "v1.0.4"
    body = fetch(tag)

    print(f"{tag}: {len(body)} 字符")
    print(f"  真正的换行 LF: {body.count(NEWLINE)}")
    print(f"  真正的回车 CR: {body.count(CARRIAGE)}")

    needle = BACKSLASH + LETTER_N
    count = body.count(needle)
    print(f"  字面 {BACKSLASH}{LETTER_N} 组合: {count}")

    index = 0
    for hit in range(min(count, 6)):
        index = body.find(needle, index)
        context = body[max(0, index - 60):index + 60]
        rendered = "".join(
            {
                NEWLINE: "«LF»",
                CARRIAGE: "«CR»",
                BACKSLASH: "«BS»",
            }.get(char, char)
            for char in context
        )
        print(f"\n  命中 {hit + 1} @ {index}:")
        print(f"    {rendered}")
        index += 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
