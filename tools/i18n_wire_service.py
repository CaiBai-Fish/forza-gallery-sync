"""把服务层的中文文案接到 StringLocalizer（键见 i18n_fix_service_msgs.py）。

只处理**整串字面量**的中文 → StringLocalizer.Get/Format；
日志（Logger.*）与诊断文案不动（翻译会让日志检索变难）。

用法：E:\\conda\\envs\\FGS\\python.exe tools\\i18n_wire_service.py
"""
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"

# (文件, 原文, 键, 是否用 Format)
RULES: list[tuple[str, str, str, bool]] = [
    ("web/Services/UpdateService.cs", "缺少目标版本号，无法构造下载地址。", "Upd_NoVersion", False),
    ("web/Services/UpdateService.cs", "下载到的文件为空。", "Upd_EmptyDownload", False),
    ("web/Services/UpdateService.cs", "更新包不存在。", "Upd_NoPackage", False),
    ("web/Services/UpdateService.cs", "更新包是空的。", "Upd_EmptyPackage", False),
    ("web/Services/UpdateService.cs",
     "更新包里找不到 forza-gallery-sync.exe，可能下载到了错误的文件。", "Upd_NoExeInPackage", False),
    ("web/Services/UpdateService.cs",
     "更新包哈希校验失败，文件可能下载不完整或已被篡改，已删除。请重试；若反复失败请到 Releases 页手动下载。",
     "Upd_HashMismatch", False),
    ("web/Services/UpdateService.cs",
     "安装程序哈希校验失败，文件可能下载不完整或已被篡改，已删除。请重试；"
     "若反复失败请到 Releases 页手动下载。", "Upd_InstallerHashMismatch", False),

    ("web/Services/IncrementalUpdateService.cs", "正在比对本地文件…", "Inc_CompareLocal", False),
    ("web/Services/IncrementalUpdateService.cs", "正在打包增量更新…", "Inc_Packing", False),
]


def main() -> int:
    total = 0
    for rel, original, key, use_format in RULES:
        path = ROOT / rel
        text = path.read_text(encoding="utf-8")
        needle = f'"{original}"'
        if needle not in text:
            print(f"  [skip] {rel}: 未找到 {original[:30]}")
            continue
        call = f'StringLocalizer.Format("{key}")' if use_format else f'StringLocalizer.Get("{key}")'
        text = text.replace(needle, call)
        path.write_text(text, encoding="utf-8", newline="\n")
        print(f"  {Path(rel).name}: 接线 {key}")
        total += 1
    print(f"合计接线 {total} 条")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
