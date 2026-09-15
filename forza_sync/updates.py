"""检查更新：按多路探测取远端最新版本，并与当前版本对比。

版本探测刻意做成**多路**，而不是只看 GitHub API。原因（实测结论）：

* ``api.github.com`` 未认证请求按**出口 IP** 限流（60 次/小时），实测经常直接 403；
* ``raw.githubusercontent.com`` 在部分网络下不可达；
* ``github.com`` 的 tag / blob 页面内嵌了文件原文，可以直接当纯文本解析出来。

因此探测顺序为：

1. ``api.github.com/repos/<repo>/releases/latest`` → ``tag_name``
2. ``api.github.com/repos/<repo>/tags`` → 第一个 ``name``
3. ``github.com/<repo>/releases/latest`` 的 302 ``Location`` → ``/releases/tag/<tag>``
4. ``github.com/<repo>/tags`` 页面 HTML → ``/releases/tag/<tag>`` 链接
5. **保底**：``CHANGELOG.md`` 里第一个 ``## [x.y.z]`` 标题
   （自身再有 raw → github.com blob 页面 → API 三级回退）

拿到版本号之后再单独取一次 CHANGELOG，用于显示"更新了哪些内容"；
五路全失败时 ``error`` 里会带上每一路的**具体原因**。

桌面端设置页的「检查更新」按钮通过 :func:`check_update` 获取这些信息；CLI 也可复用。
网络/解析失败不抛异常，而是通过返回值中的 ``error`` 字段上报。
"""

from __future__ import annotations

import html
import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

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
TAGS_PAGE_URL = f"https://github.com/{UPDATE_REPO}/tags"
BLOB_PAGE_URL = f"https://github.com/{UPDATE_REPO}/blob/main/CHANGELOG.md"

# API 仅作为 CHANGELOG 不可用时的兜底。
DEFAULT_UPDATE_URL = f"https://api.github.com/repos/{UPDATE_REPO}/releases/latest"
DEFAULT_TAGS_URL = f"https://api.github.com/repos/{UPDATE_REPO}/tags"

CHANGELOG_FILENAME = "CHANGELOG.md"

# 与 WebView/桌面端共用的超时：探测是"用户点一下等结果"，不能太久。
PROBE_TIMEOUT = 8
TOTAL_PROBE_TIMEOUT = 14

# 匹配 "## [0.5.0] - 2026-09-14" / "## [0.5.0]" / "## [Unreleased]"
_HEADING_RE = re.compile(
    r"^##\s+\[([^\]]+)\](?:\s*-\s*(\d{4}-\d{2}-\d{2}))?\s*$",
    re.MULTILINE,
)

# 版本标题（不限方括号）：CHANGELOG 里常见的 "## 0.4.0" 也要认；
# 这一条只在保底路径用作"抓第一个版本号"，宽松一点无妨。
_LOOSE_HEADING_RE = re.compile(
    r"^##\s+\[?v?(\d+\.\d+(?:\.\d+)?)\]?",
    re.MULTILINE,
)

# github.com/<repo>/tags 页面里的版本标签链接
_TAG_LINK_RE = re.compile(r'href="/' + re.escape(UPDATE_REPO) + r'/releases/tag/([^"/?#]+)"')
# /releases/latest 的最终 Location 或页面里的 tag 链接
_RELEASE_TAG_RE = re.compile(r'/releases/tag/([^"/?#]+)')
# blob 页面内嵌 JSON 里的文件正文（多个版本都试）
_RAW_LINES_KEYS = ("rawLines", "rawBlob", "blobLines")

# 可选：硬编码你自己的 GitHub token，提升 API 限流（未认证 60 次/时 → 认证 5000 次/时）。
# ⚠️ 安全提醒：请勿把真实 token 提交到仓库（泄露后请到 GitHub Developer settings 立即撤销）；
#    仅本地填入。留空时依次回退到环境变量 FORZA_SYNC_GITHUB_TOKEN / GITHUB_TOKEN。
GITHUB_TOKEN = ""


def _version_key(text: str) -> tuple:
    """版本号比较键。无法解析出版本号时返回 (0,)，使其排在所有正式版本之前。"""
    return _parse_version(text) or (0,)


def _parse_version(text: str) -> tuple:
    """把版本字符串解析为可比较的整数元组（忽略 v 前缀与 -hash 后缀）。"""
    text = (text or "").lstrip("vV").split("-", 1)[0]
    digits = re.findall(r"\d+", text)
    return tuple(int(d) for d in digits) or (0,)


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


def _short_url(url: str) -> str:
    """把 URL 压成便于阅读的一小段（错误信息里用）。"""
    return url.split("//", 1)[-1].split("/", 1)[0]


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


# ---------------------------------------------------------------------------
# 探测：五路取版本号
# ---------------------------------------------------------------------------


class _Probe:
    """一次探测的上下文：统一的请求头、逐次超时与总体时间预算。

    总体预算的意义：五路里前几路可能都超时，但用户只点了一次按钮，
    不能让整件事拖到 5×8 秒。超预算后剩下的路径会立刻记为"预算耗尽"，
    而不是静默跳过——错误摘要里要看得见是哪几路没跑。
    """

    def __init__(self, cfg: Any) -> None:
        self.verify_ssl = getattr(cfg, "verify_ssl", True)
        self.user_agent = getattr(cfg, "user_agent", "") or f"forza-sync/{__version__}"
        self.failures: List[str] = []
        self._deadline = time.monotonic() + TOTAL_PROBE_TIMEOUT

    def fail(self, source: str, detail: str) -> None:
        self.failures.append(f"{source}: {detail}")

    def get(self, url: str, *, accept: str = "", allow_redirects: bool = True,
            timeout: int = PROBE_TIMEOUT) -> "requests.Response":
        """带统一请求头与超时的 GET；超出总预算按超时处理。"""
        budget = self._deadline - time.monotonic()
        if budget <= 0.5:
            raise TimeoutError("探测总时间预算已耗尽")
        remaining_total = max(1, int(budget))

        headers = {"User-Agent": self.user_agent}
        if accept:
            headers["Accept"] = accept
        token = (
            GITHUB_TOKEN
            or os.environ.get("FORZA_SYNC_GITHUB_TOKEN")
            or os.environ.get("GITHUB_TOKEN")
        )
        if token and "api.github.com" in url:
            headers["Authorization"] = f"Bearer {token}"

        return requests.get(
            url,
            timeout=max(1, min(timeout, remaining_total)),
            verify=self.verify_ssl,
            headers=headers,
            allow_redirects=allow_redirects,
        )

    def describe(self, url: str, exc: BaseException) -> str:
        """异常 → 可读原因（403 / 超时 / 不可达 要能区分开）。"""
        if isinstance(exc, requests.exceptions.Timeout):
            return f"{_short_url(url)} 请求超时"
        if isinstance(exc, requests.exceptions.SSLError):
            return f"{_short_url(url)} TLS 校验失败"
        if isinstance(exc, requests.exceptions.ConnectionError):
            return f"{_short_url(url)} 不可达"
        if isinstance(exc, requests.exceptions.HTTPError):
            resp = getattr(exc, "response", None)
            status = getattr(resp, "status_code", "?")
            if status in (403, 429):
                return f"{_short_url(url)} 被拒绝（HTTP {status}，多半是 API 限流）"
            return f"{_short_url(url)} 返回 HTTP {status}"
        return f"{_short_url(url)} {str(exc) or exc.__class__.__name__}"


def _normalize_tag(tag: str) -> str:
    """tag → 版本号（去掉 v 前缀与手动触发留下的 -<短提交号> 后缀）。"""
    value = (tag or "").strip().lstrip("vV")
    # 手动触发产生的 tag 形如 "1.0.2-abc1234"：只保留版本段
    match = re.match(r"(\d+(?:\.\d+)*)", value)
    return match.group(1) if match else value


def _is_release_tag(tag: str) -> bool:
    """是否是发布 tag（过滤 CI 的临时 tag、手写标签等）。"""
    return bool(re.match(r"^v?\d+\.\d+(\.\d+)?(-[0-9a-f]{7,})?$", (tag or "").strip()))


def _extract_raw_lines(payload: Any) -> Optional[List[str]]:
    """在 blob 页面的内嵌 JSON 里递归找文件正文行数组。"""
    if isinstance(payload, dict):
        for key in _RAW_LINES_KEYS:
            value = payload.get(key)
            if isinstance(value, list) and value and all(isinstance(x, str) for x in value):
                return value
        for value in payload.values():
            found = _extract_raw_lines(value)
            if found is not None:
                return found
    elif isinstance(payload, list):
        for item in payload:
            found = _extract_raw_lines(item)
            if found is not None:
                return found
    return None


def _changelog_text_from_blob_html(page: str) -> str:
    """从 github.com/blob 页面里取出文件原文。

    GitHub 会把文件内容塞进
    ``<script type="application/json" data-target="react-app.embeddedData">``
    的 ``payload...StyledBlob.rawLines``（每行一个字符串）。
    这条路径的意义：``raw.githubusercontent.com`` 不可达时，blob 页面常常还能打开。
    """
    for match in re.finditer(
        r'<script[^>]*type="application/json"[^>]*>(.*?)</script>', page or "", re.DOTALL
    ):
        blob = match.group(1).strip()
        if not blob or "rawLines" not in blob:
            continue
        try:
            payload = json.loads(html.unescape(blob))
        except (ValueError, TypeError):
            continue
        lines = _extract_raw_lines(payload)
        if lines:
            return "\n".join(lines)

    # 兜底：结构变了就直接从 HTML 里抓行对象
    lines = re.findall(r'\{"line":"(.*?)"\}', page or "")
    return "\n".join(json.loads(f'"{line}"') for line in lines) if lines else ""


def _first_loose_version(changelog_text: str) -> str:
    """保底提取：第一个版本标题（宽松匹配，连 "## 0.4.0" 也认）。"""
    parsed = parse_changelog(changelog_text)
    if parsed["latest"]:
        return parsed["latest"]

    match = _LOOSE_HEADING_RE.search(changelog_text or "")
    return match.group(1) if match else ""


def probe_release_api(probe: _Probe) -> Optional[Tuple[str, str]]:
    """路径 1：Releases API 的 ``tag_name``。"""
    source = "releases API"
    try:
        resp = probe.get(DEFAULT_UPDATE_URL, accept="application/vnd.github+json")
        if resp.status_code in (403, 429):
            remaining = resp.headers.get("X-RateLimit-Remaining")
            if resp.status_code == 429 or remaining == "0":
                probe.fail(source, _rate_limit_message(resp))
                return None
        if resp.status_code == 404:
            probe.fail(source, "仓库还没有任何 Release")
            return None
        resp.raise_for_status()
        tag = (resp.json() or {}).get("tag_name") or ""
        version = _normalize_tag(tag)
        if not version:
            probe.fail(source, "响应里没有 tag_name")
            return None
        return version, f"https://github.com/{UPDATE_REPO}/releases/tag/{tag}"
    except Exception as exc:  # noqa: BLE001 换下一路
        probe.fail(source, probe.describe(DEFAULT_UPDATE_URL, exc))
        return None


def probe_tags_api(probe: _Probe) -> Optional[Tuple[str, str]]:
    """路径 2：Tags API 的第一个 ``name``。"""
    source = "tags API"
    try:
        resp = probe.get(DEFAULT_TAGS_URL, accept="application/vnd.github+json")
        if resp.status_code in (403, 429) and resp.headers.get("X-RateLimit-Remaining") == "0":
            probe.fail(source, _rate_limit_message(resp))
            return None
        resp.raise_for_status()
        entries = resp.json() or []
        for entry in entries if isinstance(entries, list) else []:
            tag = (entry or {}).get("name") or ""
            if _is_release_tag(tag):
                return _normalize_tag(tag), f"https://github.com/{UPDATE_REPO}/releases/tag/{tag}"
        probe.fail(source, "没有形如 x.y.z 的 tag")
        return None
    except Exception as exc:  # noqa: BLE001 换下一路
        probe.fail(source, probe.describe(DEFAULT_TAGS_URL, exc))
        return None


def probe_releases_redirect(probe: _Probe) -> Optional[Tuple[str, str]]:
    """路径 3：``/releases/latest`` 的 302 Location。

    没有 Release 时 GitHub 会跳到 ``/releases``（正则匹配不到 tag），此时换下一路。
    """
    source = "releases/latest 跳转"
    try:
        resp = probe.get(f"https://github.com/{UPDATE_REPO}/releases/latest", allow_redirects=False)
        location = resp.headers.get("Location") or ""
        match = _RELEASE_TAG_RE.search(location)

        if not match:
            # 有些网络环境下不返回 302 而是直接 200 渲染页面，再从正文里找一次
            if resp.status_code == 200:
                match = _RELEASE_TAG_RE.search(resp.text or "")

        if not match:
            probe.fail(source, "没有跳转到 /releases/tag/<tag>（可能还没有 Release）")
            return None
        tag = match.group(1)
        return _normalize_tag(tag), f"https://github.com/{UPDATE_REPO}/releases/tag/{tag}"
    except Exception as exc:  # noqa: BLE001 换下一路
        probe.fail(source, probe.describe(f"https://github.com/{UPDATE_REPO}/releases/latest", exc))
        return None


def probe_tags_html(probe: _Probe) -> Optional[Tuple[str, str]]:
    """路径 4：``/tags`` 页面 HTML 里的 ``/releases/tag/<tag>`` 链接。"""
    source = "tags 页面"
    try:
        resp = probe.get(TAGS_PAGE_URL)
        resp.raise_for_status()
        for tag in _TAG_LINK_RE.findall(resp.text or ""):
            if _is_release_tag(tag):
                return _normalize_tag(tag), f"https://github.com/{UPDATE_REPO}/releases/tag/{tag}"
        probe.fail(source, "页面里没有形如 x.y.z 的 tag 链接")
        return None
    except Exception as exc:  # noqa: BLE001 换下一路
        probe.fail(source, probe.describe(TAGS_PAGE_URL, exc))
        return None


def probe_changelog(probe: _Probe) -> Optional[Tuple[str, str]]:
    """路径 5（保底）：CHANGELOG 的第一个 ``## [x.y.z]`` 标题。

    自身三级回退：raw（含 CDN 镜像）→ github.com blob 页面 → 本地副本。
    """
    source = "CHANGELOG"
    override = os.environ.get("FORZA_SYNC_CHANGELOG_URL")
    urls = (override,) if override else CHANGELOG_URLS

    for url in urls:
        try:
            resp = probe.get(url)
            resp.raise_for_status()
            resp.encoding = resp.encoding or "utf-8"
            version = _first_loose_version(resp.text)
            if version:
                return version, RELEASES_PAGE_URL
            probe.fail(source, f"{_short_url(url)} 里没有可识别的版本号")
        except Exception as exc:  # noqa: BLE001 换下一个镜像
            probe.fail(source, probe.describe(url, exc))

    # raw 都不通：试 github.com 的 blob 页面（内嵌 JSON 含原文）
    if not override:
        try:
            resp = probe.get(BLOB_PAGE_URL)
            resp.raise_for_status()
            version = _first_loose_version(_changelog_text_from_blob_html(resp.text))
            if version:
                return version, RELEASES_PAGE_URL
            probe.fail(source, "blob 页面里没有可取出的版本号")
        except Exception as exc:  # noqa: BLE001 继续本地
            probe.fail(source, probe.describe(BLOB_PAGE_URL, exc))

    # 最后用本地副本（离线时也能判断，但可能落后于远端）
    local = _find_changelog()
    if local is not None:
        try:
            version = _first_loose_version(local.read_text(encoding="utf-8"))
            if version:
                return version, RELEASES_PAGE_URL
            probe.fail(source, f"本地 {local} 里没有可识别的版本号")
        except OSError as exc:
            probe.fail(source, f"本地 {local} 读取失败: {exc}")
    else:
        probe.fail(source, "本地没有 CHANGELOG.md")

    return None


# 探测顺序即优先级；前四路拿版本号，第五路保底。
PROBES = (
    ("releases-api", probe_release_api),
    ("tags-api", probe_tags_api),
    ("releases-redirect", probe_releases_redirect),
    ("tags-page", probe_tags_html),
    ("changelog", probe_changelog),
)

# 主分支上 CHANGELOG 的镜像与 blob 页面（与路径 5 共用 5.1/5.2，5.3 用 API）。
_CHANGELOG_API_URL = f"https://api.github.com/repos/{UPDATE_REPO}/contents/{CHANGELOG_FILENAME}?ref=main"


def fetch_changelog(probe: _Probe) -> Tuple[str, str]:
    """取远端 CHANGELOG 正文（用于展示"更新了哪些内容"）。

    返回 ``(正文, 来源)``；全部失败时返回 ``("", "")``——版本判断不依赖它，
    所以取不到只影响更新日志显示，不影响"有没有新版本"的结论。
    """
    override = os.environ.get("FORZA_SYNC_CHANGELOG_URL")
    urls = (override,) if override else CHANGELOG_URLS

    for url in urls:
        try:
            resp = probe.get(url, timeout=6)
            resp.raise_for_status()
            resp.encoding = resp.encoding or "utf-8"
            if _first_loose_version(resp.text):
                return resp.text, _short_url(url)
        except Exception:  # noqa: BLE001 换下一个镜像/路径
            continue

    if not override:
        try:
            resp = probe.get(BLOB_PAGE_URL, timeout=6)
            resp.raise_for_status()
            text = _changelog_text_from_blob_html(resp.text)
            if _first_loose_version(text):
                return text, "github.com blob"
        except Exception:  # noqa: BLE001 换下一路
            pass

    try:
        # contents API 返回 base64 正文；这是最后一路，能用就不用本地副本
        import base64

        resp = probe.get(_CHANGELOG_API_URL, accept="application/vnd.github+json", timeout=6)
        resp.raise_for_status()
        content = ((resp.json() or {}).get("content") or "").replace("\n", "")
        if content:
            text = base64.b64decode(content).decode("utf-8", "replace")
            if _first_loose_version(text):
                return text, "contents API"
    except Exception:  # noqa: BLE001 落到本地
        pass

    local = _find_changelog()
    if local is not None:
        try:
            text = local.read_text(encoding="utf-8")
            if _first_loose_version(text):
                return text, f"本地 {local.name}"
        except OSError:
            pass

    return "", ""


def check_update(config_path: Optional[str] = None) -> Dict[str, Any]:
    """检查是否有新版本。

    返回字段：

    ``current`` / ``latest`` / ``has_update`` / ``url`` / ``name`` /
    ``published_at`` / ``notes`` / ``source`` / ``error``，
    另加 ``probe_source``（本次实际命中的探测路径）与 ``attempts``（每一路的结果摘要）。

    网络/解析失败不抛异常，通过 ``error`` 字段上报，并保留
    ``RELEASES_PAGE_URL`` 作为"打开发布页"的手动出口。
    """
    mgr = ConfigManager(Path(config_path) if config_path else None)
    cfg = mgr.load()

    probe = _Probe(cfg)

    result: Dict[str, Any] = {
        "current": __version__,
        "latest": "",
        "has_update": False,
        "url": RELEASES_PAGE_URL,
        "name": "",
        "published_at": "",
        "notes": "",
        "source": "",
        "probe_source": "",
        "attempts": [],
        "error": "",
    }

    found: Optional[Tuple[str, str, str]] = None
    for name, func in PROBES:
        before = len(probe.failures)
        outcome = func(probe)
        added = probe.failures[before:]
        if outcome is None:
            result["attempts"].append({
                "source": name,
                "ok": False,
                "reason": "；".join(added),
            })
            continue
        version, url = outcome
        found = (name, version, url)
        result["attempts"].append({"source": name, "ok": True, "version": version})
        break

    if found is None:
        reason = "；".join(probe.failures) or "所有探测路径都不可用"
        result["error"] = f"无法获取最新版本（{reason}）。可点击「打开发布页」手动查看。"
        return result

    name, version, url = found
    result["latest"] = version
    result["url"] = url or RELEASES_PAGE_URL
    result["name"] = f"v{version}"
    result["probe_source"] = name
    result["source"] = name
    result["has_update"] = _version_key(version) > _version_key(__version__)

    # 更新说明：单独再取一次 CHANGELOG（取不到不影响版本判断）
    changelog_text, changelog_from = fetch_changelog(probe)
    if changelog_text:
        parsed = parse_changelog(changelog_text)
        result["notes"] = parsed["sections"].get(version, "")
        result["published_at"] = parsed["dates"].get(version, "")
        if result["notes"]:
            result["source"] = f"{name}+{changelog_from}"

    if not result["notes"]:
        # 没有该版本的小节时给一句可读说明，而不是丢一个空白区域
        result["notes"] = "" if not result["has_update"] else (
            f"发布说明未包含 v{version} 小节，可点击「打开发布页」查看。"
        )

    return result
