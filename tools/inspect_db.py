"""统计残留索引库里的记录数，判断是否值得保留。

用法: python tools/inspect_db.py "<数据库路径>"
"""
from __future__ import annotations

import sqlite3
import sys
from pathlib import Path


def main() -> int:
    path = Path(sys.argv[1])
    if not path.exists():
        print(f"[FAIL] 不存在: {path}")
        return 1

    print(f"文件: {path}  ({path.stat().st_size} 字节)")
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    try:
        tables = [
            row[0]
            for row in conn.execute(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"
            )
        ]
        print(f"表: {', '.join(tables) or '（无）'}")
        total = 0
        for table in tables:
            count = conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
            total += count
            print(f"  {table:<16} {count} 行")
        print(f"总记录数: {total}")
        print("\n结论: " + ("空库（无用户数据，删除无损失）" if total == 0 else "含用户数据，保留"))
    finally:
        conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
