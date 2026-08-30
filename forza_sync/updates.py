"""检查更新：查询 GitHub Releases 最新版本并与当前版本对比。

桌面端设置页的「检查更新」按钮通过 :func:`check_update` 获取最新版本信息；
CLI 也可复用。网络/解析失败不会抛异常，而是通过返回值中的 ``error`` 字段上报。
"""

from __future__ import annotations

import os
import re
from pathlib import Path
from typing import Any, Dict, Optional

import requests

from . import __version__
from .config import ConfigManager

# 更新检查源：GitHub Releases API（可通过环境变量 FORZA_SYNC_UPDATE_URL 覆盖，便于测试）
UPDATE_REPO = "CaiBai-Fish/forza-gallery-sync"
DEFAULT_UPDATE_URL = f"https://api.github.com/repos/{UPDATE_REPO}/releases/latest"

# 可选：硬编码你自己的 GitHub token，提升 API 限流（未认证 60 次/时 → 认证 5000 次/时）。
# ⚠️ 安全提醒：请勿把真实 token 提交到仓库（泄露后请到 GitHub Developer settings 立即撤销）；
#    仅本地填入。留空时依次回退到环境变量 FORZA_SYNC_GITHUB_TOKEN / GITHUB_TOKEN。
GITHUB_TOKEN = ""


def _parse_version(text: str) -> tuple:
    """把版本字符串解析为可比较的整数元组（忽略 v 前缀与 -hash 后缀）。"""
    text = (text or "").lstrip("vV").split("-", 1)[0]
    digits = re.findall(r"\d+", text)
    return tuple(int(d) for d in digits) or (0,)


def check_update(config_path: Optional[str] = None) -> Dict[str, Any]:
    """查询最新版本并返回对比结果；网络/解析失败不抛错，通过 error 字段返回。"""
    mgr = ConfigManager(Path(config_path) if config_path else None)
    cfg = mgr.load()

    result: Dict[str, Any] = {
        "current": __version__,
        "latest": "",
        "has_update": False,
        "url": "",
        "name": "",
        "published_at": "",
        "error": "",
    }

    url = os.environ.get("FORZA_SYNC_UPDATE_URL") or DEFAULT_UPDATE_URL
    try:
        headers = {
            "Accept": "application/vnd.github+json",
            "User-Agent": cfg.user_agent or f"forza-sync/{__version__}",
        }
        # 优先使用代码内硬编码的 token，其次回退到环境变量（避免 403 限流）。
        token = GITHUB_TOKEN or os.environ.get("FORZA_SYNC_GITHUB_TOKEN") or os.environ.get("GITHUB_TOKEN")
        if token:
            headers["Authorization"] = f"Bearer {token}"

        resp = requests.get(
            url,
            timeout=8,
            verify=cfg.verify_ssl,
            headers=headers,
        )
        resp.raise_for_status()
        data = resp.json()

        tag = data.get("tag_name") or ""
        latest = tag[1:] if tag.startswith("v") else tag
        result.update(
            latest=latest,
            url=data.get("html_url") or "",
            name=data.get("name") or data.get("tag_name") or "",
            published_at=data.get("published_at") or "",
        )
        result["has_update"] = _parse_version(latest) > _parse_version(__version__)
    except Exception as exc:  # noqa: BLE001 网络/解析失败不应导致崩溃
        result["error"] = str(exc) or exc.__class__.__name__

    return result
