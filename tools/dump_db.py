"""列出索引库中的照片与同步状态，用于确认残留数据是否值得保留。

用法: python tools/dump_db.py "<数据库路径>"
"""
from __future__ import annotations

import sqlite3
import sys
from pathlib import Path


def main() -> int:
    path = Path(sys.argv[1])
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    try:
        print("=== 表结构 ===")
        for row in conn.execute("SELECT name, sql FROM sqlite_master WHERE type='table'"):
            print(f"\n-- {row['name']}")
            print(row["sql"])

        print("\n=== photos ===")
        try:
            rows = conn.execute("SELECT * FROM photos LIMIT 20").fetchall()
            if rows:
                keys = rows[0].keys()
                print(" | ".join(keys))
                for row in rows:
                    values = []
                    for key in keys:
                        value = row[key]
                        if isinstance(value, bytes):
                            value = f"<{len(value)} 字节>"
                        text = str(value)
                        values.append(text[:46] + "…" if len(text) > 47 else text)
                    print(" | ".join(values))
            else:
                print("（无记录）")
        except Exception as exc:
            print(f"读取失败: {exc}")

        print("\n=== sync_state ===")
        for row in conn.execute("SELECT * FROM sync_state"):
            print(dict(row))
    finally:
        conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
