"""Token 管理与自动刷新。

Forza Gallery API 使用 OAuth2 刷新流程获取短时 access_token：

    POST https://api.forza.net/connect/token
    Content-Type: application/x-www-form-urlencoded
    Origin: https://forza.net

    grant_type=refresh_token
    refresh_token=<refresh_token>
    scope=openid+profile+offline_access
    client_id=nuxt-spa

响应：
    {
        "access_token": "...",   # 作为 Bearer 用于 Gallery API
        "expires_in": 3299,      # 秒（约 55 分钟）
        "refresh_token": "...",  # 轮换制：每次刷新都会换新，必须保存新值
        "id_token": "..."
    }
"""

from __future__ import annotations

import logging
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Optional

import requests

from .config import ConfigManager
from .errors import AuthError, NetworkError

log = logging.getLogger(__name__)

TOKEN_URL = "https://api.forza.net/connect/token"
CLIENT_ID = "nuxt-spa"
SCOPE = "openid profile offline_access"
GRANT_TYPE = "refresh_token"

# 提前多少秒刷新，避免到期前请求失败
REFRESH_MARGIN_SECONDS = 60


@dataclass
class TokenBundle:
    """一次刷新返回的 token 对。"""

    access_token: str
    refresh_token: str
    expires_in: int  # 秒


class TokenRefresher:
    """执行 OAuth2 refresh_token 请求（不落盘，只负责 HTTP）。"""

    def __init__(
        self,
        *,
        timeout: int = 30,
        retries: int = 3,
        verify_ssl: bool = True,
        user_agent: str = "forza-sync",
    ):
        self.timeout = timeout
        self.retries = max(1, retries)
        self.verify_ssl = verify_ssl
        self.session = requests.Session()
        self.session.headers.update(
            {
                "Accept": "application/json",
                "User-Agent": user_agent,
                "Origin": "https://forza.net",
                "Referer": "https://forza.net/",
            }
        )

    def refresh(self, refresh_token: str) -> TokenBundle:
        """用 refresh_token 换取新的 access_token（并返回轮换后的新 refresh_token）。"""
        data = {
            "grant_type": GRANT_TYPE,
            "refresh_token": refresh_token,
            "scope": SCOPE,
            "client_id": CLIENT_ID,
        }
        last_exc: Optional[Exception] = None
        for attempt in range(1, self.retries + 1):
            try:
                resp = self.session.post(
                    TOKEN_URL, data=data, timeout=self.timeout, verify=self.verify_ssl
                )
            except requests.exceptions.RequestException as exc:
                last_exc = NetworkError(f"Token 刷新网络异常: {exc}")
                log.warning("Token 刷新网络异常（第 %d/%d 次）: %s", attempt, self.retries, exc)
                time.sleep(min(2**attempt, 10))
                continue

            if resp.status_code in (400, 401):
                # refresh_token 失效、过期或被吊销
                raise AuthError(
                    "refresh_token 已失效或过期，请重新登录获取新的 refresh_token"
                )
            if resp.status_code == 429 or resp.status_code >= 500:
                if attempt < self.retries:
                    log.warning(
                        "Token 服务器返回 %s（第 %d/%d 次重试）",
                        resp.status_code,
                        attempt,
                        self.retries,
                    )
                    time.sleep(min(2**attempt, 10))
                    continue
                resp.raise_for_status()

            resp.raise_for_status()
            try:
                payload = resp.json()
            except ValueError as exc:
                raise AuthError(f"Token 刷新返回的不是合法 JSON: {exc}") from exc

            access_token = payload.get("access_token")
            if not isinstance(access_token, str) or not access_token.strip():
                raise AuthError("Token 刷新响应缺少 access_token")
            # 轮换制：优先使用新 refresh_token；缺失时保留旧的
            new_refresh = payload.get("refresh_token")
            if not isinstance(new_refresh, str) or not new_refresh.strip():
                new_refresh = refresh_token
            try:
                expires_in = int(payload.get("expires_in", 0))
            except (TypeError, ValueError):
                expires_in = 0
            return TokenBundle(
                access_token=access_token,
                refresh_token=new_refresh,
                expires_in=expires_in,
            )

        raise last_exc if last_exc else AuthError("Token 刷新失败")


class TokenManager:
    """管理 access_token / refresh_token 的持久化与自动刷新。"""

    def __init__(
        self,
        config_path=None,
        *,
        timeout: int = 30,
        retries: int = 3,
        verify_ssl: bool = True,
        user_agent: str = "forza-sync",
    ):
        self.mgr = ConfigManager(config_path)
        self.refresher = TokenRefresher(
            timeout=timeout,
            retries=retries,
            verify_ssl=verify_ssl,
            user_agent=user_agent,
        )
        self._cached: Optional[str] = None  # 进程内缓存

    # ------------------------------------------------------------------
    def access_token(self) -> str:
        """返回可用的 access token；已过期且配置了 refresh_token 时自动刷新。"""
        cfg = self.mgr.load()
        if self._cached:
            return self._cached
        if self._is_expired(cfg):
            if cfg.refresh_token:
                self._cached = self.refresh()
                return self._cached
            if not cfg.token:
                raise AuthError("未配置 Token，请运行 `forza-sync config`")
        return cfg.token

    def refresh(self) -> str:
        """强制刷新并持久化新的 token 对，返回新 access token。"""
        cfg = self.mgr.load()
        if not cfg.refresh_token:
            raise AuthError(
                "未配置 refresh_token，无法自动刷新。"
                "请运行 `forza-sync config` 输入 refresh_token"
            )
        bundle = self.refresher.refresh(cfg.refresh_token)
        cfg.token = bundle.access_token
        cfg.refresh_token = bundle.refresh_token
        cfg.token_issued_at = datetime.now(timezone.utc).isoformat()
        cfg.token_expires_in = bundle.expires_in
        self.mgr.save(cfg)
        self._cached = bundle.access_token
        log.info("Token 已自动刷新（有效期约 %d 秒）", bundle.expires_in)
        return bundle.access_token

    def status(self) -> dict:
        """返回 token 状态信息（供 status / token 命令展示）。"""
        cfg = self.mgr.load()
        info = {
            "has_token": bool(cfg.token),
            "has_refresh_token": bool(cfg.refresh_token),
            "expired": self._is_expired(cfg),
            "expires_in": None,
            "masked_token": _mask(cfg.token),
            "masked_refresh_token": _mask(cfg.refresh_token),
        }
        if cfg.token_issued_at and cfg.token_expires_in:
            try:
                issued = datetime.fromisoformat(cfg.token_issued_at)
                if issued.tzinfo is None:
                    issued = issued.replace(tzinfo=timezone.utc)
                expires_at = issued + timedelta(seconds=cfg.token_expires_in)
                info["expires_in"] = max(
                    0, int((expires_at - datetime.now(timezone.utc)).total_seconds())
                )
            except (ValueError, TypeError):
                pass
        return info

    # ------------------------------------------------------------------
    @staticmethod
    def _is_expired(cfg) -> bool:
        if not cfg.token:
            return True
        if not cfg.token_issued_at or not cfg.token_expires_in:
            # 未知签发时间：不预判，交由 401 触发刷新
            return False
        try:
            issued = datetime.fromisoformat(cfg.token_issued_at)
        except ValueError:
            return False
        if issued.tzinfo is None:
            issued = issued.replace(tzinfo=timezone.utc)
        age = (datetime.now(timezone.utc) - issued).total_seconds()
        return age >= (cfg.token_expires_in - REFRESH_MARGIN_SECONDS)


def _mask(value: str, keep: int = 4) -> str:
    if not value:
        return "(未设置)"
    if len(value) <= keep * 2:
        return "*" * len(value)
    return f"{value[:keep]}{'*' * (len(value) - keep * 2)}{value[-keep:]}"
