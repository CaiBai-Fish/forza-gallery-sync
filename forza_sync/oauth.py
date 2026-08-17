"""标准 OAuth 2.0 授权码 + PKCE 流程客户端。

参考微软身份平台授权码流程文档：
https://learn.microsoft.com/zh-cn/entra/identity-platform/v2-oauth2-auth-code-flow

Forza 授权服务器为 OpenIddict（api.forza.net/connect/authorize + /connect/token），
其外部身份提供方是 Microsoft（login.live.com）。本模块实现标准授权码流程：

1. 生成 PKCE code_verifier / code_challenge（S256）
2. 浏览器打开授权端点（带 client_id / redirect_uri / state / code_challenge / nonce）
3. 用户在 Microsoft 登录页完成认证（支持两步验证）
4. 浏览器被重定向回 redirect_uri 并携带 code + state
5. 用 code + code_verifier 在令牌端点换取 access_token + refresh_token

说明：nuxt-spa 客户端白名单内的 redirect_uri 为 https://forza.net/callback，
本地回环地址（http://localhost/127.0.0.1）不被该客户端接受，
因此授权码需通过浏览器重定向捕获，而非本地回调服务器。
"""

from __future__ import annotations

import base64
import hashlib
import logging
import secrets
import time
from typing import Optional
from urllib.parse import parse_qs, urlencode, urlparse

import requests

from .auth import TokenBundle
from .errors import AuthError, NetworkError

log = logging.getLogger(__name__)

AUTHORIZE_URL = "https://api.forza.net/connect/authorize"
TOKEN_URL = "https://api.forza.net/connect/token"
CLIENT_ID = "nuxt-spa"
# nuxt-spa 客户端白名单内的 redirect_uri（本地回环地址不被接受）
REDIRECT_URI = "https://forza.net/callback"
SCOPE = "openid profile offline_access"
CODE_CHALLENGE_METHOD = "S256"


# ---------------------------------------------------------------------------
# PKCE（RFC 7636）
# ---------------------------------------------------------------------------
def generate_code_verifier() -> str:
    """生成 code_verifier（43-128 位 base64url，无填充）。"""
    return base64.urlsafe_b64encode(secrets.token_bytes(48)).rstrip(b"=").decode()


def compute_code_challenge(verifier: str) -> str:
    """按 S256 计算 code_challenge。"""
    digest = hashlib.sha256(verifier.encode("utf-8")).digest()
    return base64.urlsafe_b64encode(digest).rstrip(b"=").decode()


def build_authorize_url(
    *,
    state: str,
    code_challenge: str,
    nonce: Optional[str] = None,
    authorize_url: str = AUTHORIZE_URL,
    client_id: str = CLIENT_ID,
    redirect_uri: str = REDIRECT_URI,
    scope: str = SCOPE,
) -> str:
    """构造授权端点 URL（response_type=code + PKCE）。"""
    params = {
        "client_id": client_id,
        "redirect_uri": redirect_uri,
        "response_type": "code",
        "scope": scope,
        "state": state,
        "code_challenge": code_challenge,
        "code_challenge_method": CODE_CHALLENGE_METHOD,
    }
    if nonce:
        params["nonce"] = nonce
    return f"{authorize_url}?{urlencode(params)}"


def parse_redirect(url: str, expected_state: str) -> Optional[str]:
    """从回调 URL 中提取授权码并校验 state；成功返回 code，否则 None。"""
    qs = parse_qs(urlparse(url).query)
    code = qs.get("code", [""])[0]
    if not code:
        return None
    if qs.get("state", [""])[0] != expected_state:
        log.warning("回调 state 不匹配（可能为 CSRF 或过期回调），忽略")
        return None
    return code


# ---------------------------------------------------------------------------
# 授权码交换
# ---------------------------------------------------------------------------
def exchange_code(
    *,
    code: str,
    code_verifier: str,
    client_id: str = CLIENT_ID,
    redirect_uri: str = REDIRECT_URI,
    token_url: str = TOKEN_URL,
    timeout: int = 30,
    retries: int = 3,
    verify_ssl: bool = True,
    user_agent: str = "forza-sync",
) -> TokenBundle:
    """用授权码 + code_verifier 换取 access_token / refresh_token。

    注意：OpenIddict（ID2074）不允许在 authorization_code 交换请求中携带
    scope 参数（scope 已在授权阶段绑定），因此这里不发送 scope。
    """
    data = {
        "grant_type": "authorization_code",
        "code": code,
        "redirect_uri": redirect_uri,
        "client_id": client_id,
        "code_verifier": code_verifier,
    }
    headers = {
        "Accept": "application/json",
        "User-Agent": user_agent,
        "Origin": "https://forza.net",
        "Referer": "https://forza.net/",
    }

    last_exc: Optional[Exception] = None
    for attempt in range(1, retries + 1):
        try:
            resp = requests.post(
                token_url, data=data, headers=headers, timeout=timeout, verify=verify_ssl
            )
        except requests.exceptions.RequestException as exc:
            last_exc = NetworkError(f"授权码交换网络异常: {exc}")
            log.warning("授权码交换网络异常（第 %d/%d 次）: %s", attempt, retries, exc)
            time.sleep(min(2**attempt, 10))
            continue

        if resp.status_code in (400, 401):
            raise AuthError(
                "授权码交换失败：code 无效、已过期或已被使用。请重新运行 login 登录。"
            )
        if resp.status_code == 429 or resp.status_code >= 500:
            if attempt < retries:
                log.warning(
                    "令牌服务器返回 %s（第 %d/%d 次重试）", resp.status_code, attempt, retries
                )
                time.sleep(min(2**attempt, 10))
                continue
            resp.raise_for_status()

        resp.raise_for_status()
        try:
            payload = resp.json()
        except ValueError as exc:
            raise AuthError(f"授权码交换返回的不是合法 JSON: {exc}") from exc

        access = payload.get("access_token")
        if not isinstance(access, str) or not access:
            raise AuthError("授权码交换响应缺少 access_token")
        refresh = payload.get("refresh_token")
        if not isinstance(refresh, str) or not refresh:
            refresh = ""
        try:
            expires_in = int(payload.get("expires_in") or 0)
        except (TypeError, ValueError):
            expires_in = 0
        return TokenBundle(access_token=access, refresh_token=refresh, expires_in=expires_in)

    raise last_exc if last_exc else AuthError("授权码交换失败")
