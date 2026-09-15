"""补完用户可见的服务层文案（主要是更新失败提示）。

这些消息会经 SettingsViewModel.UpdateActionMsg 显示给用户，属于界面文案；
而同文件里的 Logger.* 与诊断文案不在此列（翻译只会让日志检索变难）。

用法：E:\\conda\\envs\\FGS\\python.exe tools\\i18n_fix_service_msgs.py
"""
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"

# 键 → (中文, English)
STRINGS: dict[str, tuple[str, str]] = {
    "Upd_NoVersion": ("缺少目标版本号，无法构造下载地址。", "Missing target version; cannot build the download URL."),
    "Upd_NoManifestZip": ("无法取得 v{0} 的哈希清单（hashes 分支的 {0}.txt / hashes.json 与 Release API 都不可用），为安全起见不自动安装。请到 Releases 页手动下载，或稍后重试。",
                          "Cannot obtain the hash manifest for v{0} (neither the hashes branch nor the Release API is reachable); refusing to install automatically for safety. Download manually from the releases page, or retry later."),
    "Upd_HashMismatch": ("更新包哈希校验失败，文件可能下载不完整或已被篡改，已删除。请重试；若反复失败请到 Releases 页手动下载。",
                         "Hash verification of the update package failed; the file may be incomplete or tampered with, and has been deleted. Retry, or download manually from the releases page."),
    "Upd_NoManifestInstaller": ("无法取得 {0} 的哈希清单（hashes 分支清单与 Release API 都不可用），为安全起见不自动安装。请到 Releases 页手动下载，或稍后重试。",
                                "Cannot obtain the hash manifest for {0} (neither the hashes branch nor the Release API is reachable); refusing to install automatically for safety. Download manually from the releases page, or retry later."),
    "Upd_InstallerHashMismatch": ("安装程序哈希校验失败，文件可能下载不完整或已被篡改，已删除。请重试；若反复失败请到 Releases 页手动下载。",
                                  "Hash verification of the installer failed; the file may be incomplete or tampered with, and has been deleted. Retry, or download manually from the releases page."),
    "Upd_DownloadFailed": ("下载失败：HTTP {0}。地址：{1}", "Download failed: HTTP {0}. URL: {1}"),
    "Upd_EmptyDownload": ("下载到的文件为空。", "The downloaded file is empty."),
    "Upd_NoPackage": ("更新包不存在。", "The update package does not exist."),
    "Upd_EmptyPackage": ("更新包是空的。", "The update package is empty."),
    "Upd_NoExeInPackage": ("更新包里找不到 forza-gallery-sync.exe，可能下载到了错误的文件。",
                           "forza-gallery-sync.exe was not found in the update package; the wrong file may have been downloaded."),
    "Upd_CorruptPackage": ("更新包已损坏或不完整：{0}", "The update package is corrupt or incomplete: {0}"),
    "Upd_DownloadPackageFailed": ("下载更新包失败：{0}", "Failed to download the update package: {0}"),
    "Upd_StartScriptFailed": ("启动更新脚本失败：{0}", "Failed to start the update script: {0}"),
    "Upd_StartInstallScriptFailed": ("启动安装脚本失败：{0}", "Failed to start the install script: {0}"),
    "Upd_BuildInstallScriptFailed": ("生成安装脚本失败：{0}", "Failed to generate the install script: {0}"),
    "Upd_BuildUpdateScriptFailed": ("生成更新脚本失败：{0}", "Failed to generate the update script: {0}"),
    "Upd_ScriptResourceMissing": ("找不到内嵌的脚本资源 {0}。", "Embedded script resource {0} was not found."),
    "Upd_ScriptResourceOpenFailed": ("无法打开内嵌的脚本资源 {0}。", "Cannot open embedded script resource {0}."),
    "Upd_DirNotWritable": ("程序目录不可写（{0}）：{1}。请手动下载更新包替换。",
                           "The program folder is not writable ({0}): {1}. Please download the update manually."),
    "Inc_CompareLocal": ("正在比对本地文件…", "Comparing local files…"),
    "Inc_CompareLocalProgress": ("正在比对本地文件… {0}/{1}", "Comparing local files… {0}/{1}"),
    "Inc_DownloadFile": ("正在下载增量文件 {0}/{1}… {2}/{3} MB", "Downloading incremental file {0}/{1}… {2}/{3} MB"),
    "Inc_DownloadSmall": ("正在下载小文件包（{0} 个文件，{1} MB）…", "Downloading the small-files bundle ({0} files, {1} MB)…"),
    "Inc_Packing": ("正在打包增量更新…", "Packing the incremental update…"),
    "Inc_NoLocalDir": ("程序目录不存在：{0}", "The program folder does not exist: {0}"),
    "Inc_MissingSmallFile": ("本地缺失的小文件不在任何容器里：{0}", "A missing local file is not covered by any bundle: {0}"),
    "Inc_EmptyAsset": ("增量资产 {0} 是空包", "Incremental asset {0} is an empty archive"),
    "Inc_AssetDownloadFailed": ("下载 {0} 失败：HTTP {1}（{2}）", "Failed to download {0}: HTTP {1} ({2})"),
    "Sync_LoadingGames": ("正在加载游戏列表…", "Loading the game list…"),
    "Format_Dimension": ("{0} × {1}", "{0} × {1}"),
    "Format_TodayTime": ("今天 {0}", "Today {0}"),
    "Format_PhotoCount": ("{0} 张", "{0}"),
}


def main() -> int:
    import json

    # 1) 写进 resw（带属性后缀，与其它条目一致）
    for lang, idx in (("zh-CN", 0), ("en-us", 1)):
        resw = WEB / "Strings" / lang / "Resources.resw"
        text = resw.read_text(encoding="utf-8")
        added = 0
        for key, (zh, en) in STRINGS.items():
            name = f"{key}.Text"
            if f'name="{name}"' in text:
                continue
            value = zh if idx == 0 else en
            entry = (f'  <data name="{name}" xml:space="preserve">\n'
                     f'    <value>{value}</value>\n  </data>\n')
            text = text.replace("</root>", entry + "</root>", 1)
            added += 1
        resw.write_text(text, encoding="utf-8", newline="\n")
        print(f"  {lang}: 新增 {added} 条")

    # 2) JSON 字典
    from subprocess import run
    run(["E:\\conda\\envs\\FGS\\python.exe", str(ROOT / "tools" / "i18n_make_json.py")], check=False)

    # 3) 报告哪些键还没在代码里使用（供人工接线参考）
    used = []
    for path in WEB.rglob("*.cs"):
        if any(p in str(path) for p in ("obj", "bin", "dist", "tools")):
            continue
        text = path.read_text(encoding="utf-8")
        for key in STRINGS:
            if key in text:
                used.append(key)
    unused = [k for k in STRINGS if k not in used]
    print(f"已接线 {len(set(used))} 条；待接线 {len(unused)} 条")
    if unused:
        print("  待接线（键已就绪，需把对应中文换成 StringLocalizer）：")
        for k in unused[:40]:
            print(f"    {k}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
