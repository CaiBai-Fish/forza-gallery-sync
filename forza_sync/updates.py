"""检查更新：查询 GitHub Releases 最新版本并与当前版本对比。

桌面端设置页的「检查更新」按钮通过 :func:`check_update` 获取最新版本信息；
CLI 也可复用。网络/解析失败不会抛异常，而是通过返回值中的 ``error`` 字段上报。
"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, Optional

import requests

from . import __version__
from .config import ConfigManager

# 更新检查源：默认读仓库根目录的 CHANGELOG.md。走静态文件下载（raw / CDN），
# 不消耗 GitHub API 配额，因此不会遇到 rate limit。
UPDATE_REPO = "CaiBai-Fish/forza-gallery-sync"

# 依次尝试，任一成功即可（raw.githubusercontent.com 在部分网络下不稳定，故备 CDN 镜像）。
CHANGELOG_URLS = (
    f"https://raw.githubusercontent.com/{UPDATE_REPO}/main/CHANGELOG.md",
    f"https://cdn.jsdelivr.net/gh/{UPDATE_REPO}@main/CHANGELOG.md",
    f"https://fastly.jsdelivr.net/gh/{UPDATE_REPO}@main/CHANGELOG.md",
)

DEFAULT_CHANGELOG_URL = CHANGELOG_URLS[0]
RELEASES_PAGE_URL = f"https://github.com/{UPDATE_REPO}/releases"

# API 仅作为 CHANGELOG 不可用时的兜底。
DEFAULT_UPDATE_URL = f"https://api.github.com/repos/{UPDATE_REPO}/releases/latest"

CHANGELOG_FILENAME = "CHANGELOG.md"

# 匹配 "## [0.5.0] - 2026-09-14" / "## [0.5.0]" / "## [Unreleased]"
_HEADING_RE = re.compile(
    r"^##\s+\[([^\]]+)\](?:\s*-\s*(\d{4}-\d{2}-\d{2}))?\s*$",
    re.MULTILINE,
)

# 可选：硬编码你自己的 GitHub token，提升 API 限流（未认证 60 次/时 → 认证 5000 次/时）。
# ⚠️ 安全提醒：请勿把真实 token 提交到仓库（泄露后请到 GitHub Developer settings 立即撤销）；
#    仅本地填入。留空时依次回退到环境变量 FORZA_SYNC_GITHUB_TOKEN / GITHUB_TOKEN。
GITHUB_TOKEN = ""


def _version_key(text: str) -> tuple:
    """版本号比较键。无法解析出版本号时返回 (0,)，使其排在所有正式版本之前。"""
    return _parse_version(text) or (0,)


def parse_changelog(text: str) -> Dict[str, Any]:
    """把 CHANGELOG 文本解析成「最新正式版 + 各版本说明」。

    只认形如 ``## [x.y.z]`` 的正式版本标题，「Unreleased / 未发布」一律跳过；
    返回的 ``sections`` 是「版本号 → 该版本正文」，供界面展示更新内容。
    """
    headings = list(_HEADING_RE.finditer(text or ""))

    versions: list = []
    sections: Dict[str, str] = {}
    dates: Dict[str, str] = {}

    for index, match in enumerate(headings):
        name = (match.group(1) or "").strip()
        if not name or not re.search(r"\d", name):
            continue  # 跳过 Unreleased / 未发布 等占位标题

        start = match.end()
        end = headings[index + 1].start() if index + 1 < len(headings) else len(text)
        sections[name] = text[start:end].strip()
        dates[name] = match.group(2) or ""
        versions.append(name)

    if not versions:
        return {"latest": "", "sections": {}, "dates": {}, "versions": []}

    latest = max(versions, key=_version_key)
    return {"latest": latest, "sections": sections, "dates": dates, "versions": versions}


def _find_changelog() -> Optional[Path]:
    """查找本地 CHANGELOG.md。

    顺序：程序/安装目录 → 可执行文件所在目录并逐级向上 → 当前工作目录 →
    `forza_sync` 包目录及其父目录（开发时即仓库根）。
    """
    seen: set = set()
    candidates: list = []

    def add(base: Optional[Path]) -> None:
        if base is None:
            return
        try:
            key = str(base)
        except Exception:  # noqa: BLE001 极端路径不作处理
            return
        if key not in seen:
            seen.add(key)
            candidates.append(base)

    add(Path(os.environ["FORZA_SYNC_APP_DIR"]) if os.environ.get("FORZA_SYNC_APP_DIR") else None)

    # 可执行文件位置（打包版：程序就在 CHANGELOG 旁边或上一级），逐级向上找。
    try:
        exe_dir = Path(sys.executable).resolve().parent
        add(exe_dir)
        add(exe_dir.parent)
    except (OSError, ValueError):
        pass

    add(Path(os.getcwd()))

    here = Path(__file__).resolve()
    add(here.parent)          # forza_sync/
    add(here.parent.parent)   # 仓库根

    for base in candidates:
        path = base / CHANGELOG_FILENAME
        if path.is_file():
            return path
    return None


def _check_update_from_changelog(changelog_text: str, result: Dict[str, Any]) -> bool:
    """用 CHANGELOG 内容填充结果；解析不出正式版本时返回 False。"""
    parsed = parse_changelog(changelog_text)
    latest = parsed["latest"]
    if not latest:
        return False

    result["latest"] = latest
    result["name"] = f"v{latest}"
    result["published_at"] = parsed["dates"].get(latest, "")
    result["notes"] = parsed["sections"].get(latest, "")
    result["url"] = RELEASES_PAGE_URL
    result["source"] = "changelog"
    result["has_update"] = _version_key(latest) > _version_key(__version__)
    return True


def _rate_limit_message(resp: "requests.Response") -> str:
    """把 GitHub 限流响应翻译成可读提示（含恢复时间）。"""
    from datetime import datetime, timezone

    reset = resp.headers.get("X-RateLimit-Reset")
    limit = resp.headers.get("X-RateLimit-Limit")
    extra = ""
    if reset and reset.isdigit():
        try:
            reset_at = datetime.fromtimestamp(int(reset), tz=timezone.utc).astimezone()
            wait = int((reset_at - datetime.now(timezone.utc)).total_seconds())
            if wait <= 0:
                extra = "，额度应已恢复，可直接重试"
            elif wait >= 3600:
                # 一小时以上用绝对时间，避免出现"约 1636842345 分钟"这种读不懂的值。
                extra = f"，预计 {reset_at:%m-%d %H:%M} 恢复"
            elif wait >= 60:
                extra = f"，约 {wait // 60} 分钟后可重试"
            else:
                extra = f"，约 {wait} 秒后可重试"
        except (TypeError, ValueError, OSError, OverflowError):
            extra = ""

    if resp.status_code == 429:
        return f"请求过于频繁（GitHub API 限流{extra}）。请稍后再试。"
    return (
        f"GitHub API 匿名请求次数已用尽（未认证上限 {limit or 60} 次/小时{extra}）。"
        "请稍后再试，或设置环境变量 FORZA_SYNC_GITHUB_TOKEN 提升到 5000 次/小时。"
    )


def _parse_version(text: str) -> tuple:
    """把版本字符串解析为可比较的整数元组（忽略 v 前缀与 -hash 后缀）。"""
    text = (text or "").lstrip("vV").split("-", 1)[0]
    digits = re.findall(r"\d+", text)
    return tuple(int(d) for d in digits) or (0,)


def check_update(config_path: Optional[str] = None) -> Dict[str, Any]:
    """检查是否有新版本。

    版本信息来源按顺序尝试：

    1. **远程 CHANGELOG.md**（默认源）：下载仓库根目录的 CHANGELOG 并解析最新正式版本。
       走 raw 下载，不消耗 GitHub API 配额，因此不会遇到 rate limit。
    2. **本地 CHANGELOG.md**：离线时回退，用随程序分发的 CHANGELOG 判断。
    3. **GitHub Releases API**：仅在 CHANGELOG 都不可用时兜底。

    网络 / 解析失败不抛异常，通过 ``error`` 字段上报。
    """
    mgr = ConfigManager(Path(config_path) if config_path else None)
    cfg = mgr.load()

    result: Dict[str, Any] = {
        "current": __version__,
        "latest": "",
        "has_update": False,
        "url": "",
        "name": "",
        "published_at": "",
        "notes": "",
        "source": "",
        "error": "",
    }

    headers = {"User-Agent": cfg.user_agent or f"forza-sync/{__version__}"}

    # ---- 1) 远程 CHANGELOG（多镜像依次尝试；不消耗 API 配额） ----
    override = os.environ.get("FORZA_SYNC_CHANGELOG_URL")
    urls = (override,) if override else CHANGELOG_URLS

    failures: list = []
    for url in urls:
        try:
            resp = requests.get(url, timeout=8, verify=cfg.verify_ssl, headers=headers)
            resp.raise_for_status()
            resp.encoding = resp.encoding or "utf-8"
            if _check_update_from_changelog(resp.text, result):
                return result
            failures.append(f"{url} 中没有可识别的版本号")
        except Exception as exc:  # noqa: BLE001 换下一个镜像
            failures.append(f"{_short_url(url)}: {exc or exc.__class__.__name__}")

    # ---- 2) 本地 CHANGELOG（开发目录 / 随程序分发的副本） ----
    local = _find_changelog()
    if local is not None:
        try:
            if _check_update_from_changelog(local.read_text(encoding="utf-8"), result):
                result["source"] = "changelog-local"
                return result
            failures.append(f"本地 {local} 中没有可识别的版本号")
        except OSError as exc:
            failures.append(f"本地 {local} 读取失败: {exc}")
    else:
        failures.append("本地没有 CHANGELOG.md")

    # ---- 3) GitHub Releases API 兜底 ----
    #
    # 只有前面都失败才会走到这里；API 有 60 次/小时的匿名限额，
    # 因此错误信息里必须带上"为什么降级"，否则只会看到一个莫名的限流提示。
    reason = "；".join(failures) or "CHANGELOG 不可用"

    api_url = os.environ.get("FORZA_SYNC_UPDATE_URL") or DEFAULT_UPDATE_URL
    try:
        api_headers = dict(headers)
        api_headers["Accept"] = "application/vnd.github+json"
        token = GITHUB_TOKEN or os.environ.get("FORZA_SYNC_GITHUB_TOKEN") or os.environ.get("GITHUB_TOKEN")
        if token:
            api_headers["Authorization"] = f"Bearer {token}"

        resp = requests.get(api_url, timeout=8, verify=cfg.verify_ssl, headers=api_headers)

        # 限流单独识别：默认的 403 报文（"rate limit exceeded"）对用户没有可操作性。
        if resp.status_code in (403, 429):
            remaining = resp.headers.get("X-RateLimit-Remaining")
            if resp.status_code == 429 or remaining == "0":
                result["error"] = f"{_rate_limit_message(resp)}（已回退到 API，原因：{reason}）"
                return result

        resp.raise_for_status()
        data = resp.json()

        tag = data.get("tag_name") or ""
        latest = tag[1:] if tag.startswith("v") else tag
        result.update(
            latest=latest,
            url=data.get("html_url") or "",
            name=data.get("name") or data.get("tag_name") or "",
            published_at=data.get("published_at") or "",
            notes=data.get("body") or "",
            source="github-api",
        )
        result["has_update"] = _version_key(latest) > _version_key(__version__)
        return result
    except Exception as exc:  # noqa: BLE001 网络/解析失败不应导致崩溃
        detail = str(exc) or exc.__class__.__name__
        result["error"] = f"{detail}（已回退到 API，原因：{reason}）"

    return result


def _short_url(url: str) -> str:
    """把 URL 压成便于阅读的一小段（错误信息里用）。"""
    return url.split("//", 1)[-1].split("/", 1)[0]
