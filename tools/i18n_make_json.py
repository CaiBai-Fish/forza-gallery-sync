"""把 Strings\\<语言>\\Resources.resw 转成 C# 侧使用的 JSON 字典。

为什么 C# 侧不用 ResourceLoader：
  未打包 WinUI 3 里 `ResourceLoader.GetString` 对本项目的 .resw 键取不到值
  （XAML 的 x:Uid 走同一份 PRI 却正常，说明是 ResourceLoader 侧的解析问题，
  不是资源没编进去）。界面文案不能依赖一个"取不到就静默回空"的 API，
  所以 C# 侧改成读一个简单的 JSON 字典——行为完全可预期，且便于人工查错。

  XAML 仍用 resw + x:Uid（那条路已验证可用），两边由本脚本保持同步：
  resw 是唯一来源，JSON 由它生成。

输出：
  web/Strings/zh-CN/strings.json
  web/Strings/en-us/strings.json
"""
from __future__ import annotations

import io
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"
STRINGS_DIR = WEB / "Strings"

LANGS = ("zh-CN", "en-us")


def read_resw(path: Path) -> dict[str, str]:
    text = path.read_text(encoding="utf-8")
    out: dict[str, str] = {}
    for m in re.finditer(r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>', text, re.DOTALL):
        name = m.group(1)
        value = (
            m.group(2)
            .replace("&amp;", "&")
            .replace("&lt;", "<")
            .replace("&gt;", ">")
        )
        out[name] = value
    return out


def main() -> int:
    for lang in LANGS:
        resw = STRINGS_DIR / lang / "Resources.resw"
        if not resw.exists():
            print(f"[skip] {resw} 不存在")
            continue

        entries = read_resw(resw)
        # x:Uid 用的资源名带属性后缀（K.Text）；C# 侧按裸键取，所以这里把后缀去掉，
        # 只保留"裸键 → 文案"。同一个键的后缀只有一个，重复时以 Text 优先。
        bare: dict[str, str] = {}
        for name, value in entries.items():
            if "." not in name:
                bare.setdefault(name, value)
                continue
            key, _, suffix = name.rpartition(".")
            if key not in bare or suffix == "Text":
                bare[key] = value

        out_path = STRINGS_DIR / lang / "strings.json"
        out_path.write_text(
            json.dumps(bare, ensure_ascii=False, indent=1) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        print(f"  {lang}: {len(bare)} 条 → {out_path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
