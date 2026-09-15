"""抽取 C# 里面向用户的界面文案（供 i18n 逐条处理）。

判定规则：
  - 只取**字符串字面量**里的中文；
  - 跳过 Logger.* 调用（日志是给开发者看的，翻译无意义，还会让日志检索变难）；
  - 跳过纯注释行；
  - 跳过 throw new UpdateException(...) 之类**面向开发/诊断**的消息？——不跳过：
    这类消息会通过界面显示给用户（例如更新失败原因），所以要翻译，
    但保留"技术细节"（HTTP 状态码、路径）不译。

输出：web/i18n-cs-report.tsv（文件、行号、上下文、文案），供人审阅后再补键。
"""
from __future__ import annotations

import io
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"

ZH = re.compile(r"[\u4e00-\u9fff]")
# 字符串字面量（普通 / 逐字 / 插值）
LITERAL = re.compile(r'(?<!\$)"([^"\\]*(?:\\.[^"\\]*)*)"|\$"([^"]*)"|@"([^"]*)"')

SKIP_PATH_PARTS = ("obj", "bin", "dist", "tools", "\\Strings\\")


def iter_cs_files():
    for path in sorted(WEB.rglob("*.cs")):
        if any(part in str(path) for part in SKIP_PATH_PARTS):
            continue
        yield path


def main() -> int:
    rows = []
    for path in iter_cs_files():
        rel = path.relative_to(ROOT).as_posix()
        lines = path.read_text(encoding="utf-8").split("\n")
        for i, line in enumerate(lines):
            stripped = line.strip()
            if stripped.startswith("//") or stripped.startswith("///") or stripped.startswith("*"):
                continue
            if "Logger." in line:
                continue
            for m in LITERAL.finditer(line):
                value = m.group(1) or m.group(2) or m.group(3) or ""
                # 插值串里的表达式部分不译；这里只关心含有中文的
                if not ZH.search(value):
                    continue
                # 纯技术文案（只有扩展名/路径/符号）跳过
                if not re.search(r"[\u4e00-\u9fff]{2,}", value):
                    continue
                rows.append((rel, i + 1, stripped[:60], value))

    out = WEB / "i18n-cs-report.tsv"
    with io.open(out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("file\tline\tcontext\ttext\n")
        for rel, ln, ctx, text in rows:
            fh.write(f"{rel}\t{ln}\t{ctx}\t{text}\n")

    print(f"抽取 {len(rows)} 条 → {out.relative_to(ROOT)}")
    from collections import Counter
    for name, count in Counter(r[0] for r in rows).most_common():
        print(f"  {name}: {count}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
