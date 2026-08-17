"""浏览器驱动的标准 OAuth 2.0 授权码 + PKCE 登录（基于 Playwright）。

按照 OAuth 2.0 授权码流程实现（参考微软身份平台文档）：
https://learn.microsoft.com/zh-cn/entra/identity-platform/v2-oauth2-auth-code-flow

1. 生成 PKCE code_verifier / code_challenge（S256）与 state
2. 打开浏览器访问授权端点 api.forza.net/connect/authorize
   —— 未登录时会被重定向到 Microsoft 登录页（login.live.com）
3. 用户登录任意 Xbox / Microsoft 账号（支持两步验证）
4. 浏览器被重定向回 redirect_uri（https://forza.net/callback）并携带 code + state
5. 用 code + code_verifier 在令牌端点换取 access_token + refresh_token
6. 保存到配置，之后由 auth.TokenManager 自动刷新续期

依赖（可选，仅在 login 命令需要）：
    pip install playwright && playwright install chromium
"""

from __future__ import annotations

import logging
import os
import secrets
import time
from pathlib import Path
from typing import Callable, Optional

from . import oauth
from .auth import TokenBundle
from .errors import AuthError

log = logging.getLogger(__name__)

MessageFn = Optional[Callable[[str], None]]


def _goto(page, url: str, timeout_ms: int = 20000) -> None:
    """带异常兜底的页面导航。"""
    try:
        page.goto(url, wait_until="domcontentloaded", timeout=timeout_ms)
    except Exception:
        pass


# ---------------------------------------------------------------------------
# 系统浏览器检测
# ---------------------------------------------------------------------------
def _find_executable(*paths: str) -> Optional[Path]:
    for p in paths:
        if p:
            candidate = Path(p)
            if candidate.exists():
                return candidate
    return None


def _find_edge() -> bool:
    pf = os.environ.get("ProgramFiles", "C:\\Program Files")
    pfx = os.environ.get("ProgramFiles(x86)", "C:\\Program Files (x86)")
    return _find_executable(
        f"{pfx}\\Microsoft\\Edge\\Application\\msedge.exe",
        f"{pf}\\Microsoft\\Edge\\Application\\msedge.exe",
    ) is not None


def _find_chrome() -> bool:
    local = os.environ.get("LOCALAPPDATA", "")
    pf = os.environ.get("ProgramFiles", "C:\\Program Files")
    pfx = os.environ.get("ProgramFiles(x86)", "C:\\Program Files (x86)")
    return _find_executable(
        f"{local}\\Google\\Chrome\\Application\\chrome.exe",
        f"{pf}\\Google\\Chrome\\Application\\chrome.exe",
        f"{pfx}\\Google\\Chrome\\Application\\chrome.exe",
    ) is not None


def _find_firefox() -> Optional[Path]:
    pf = os.environ.get("ProgramFiles", "C:\\Program Files")
    pfx = os.environ.get("ProgramFiles(x86)", "C:\\Program Files (x86)")
    local = os.environ.get("LOCALAPPDATA", "")
    return _find_executable(
        f"{pf}\\Mozilla Firefox\\firefox.exe",
        f"{pfx}\\Mozilla Firefox\\firefox.exe",
        f"{local}\\Mozilla Firefox\\firefox.exe",
    )


def detect_system_browser() -> Optional[str]:
    """检测可用的系统浏览器，按优先级返回 'msedge' / 'chrome' / 'firefox'，均无则 None。"""
    if _find_edge():
        return "msedge"
    if _find_chrome():
        return "chrome"
    if _find_firefox():
        return "firefox"
    return None


class BrowserLogin:
    """通过浏览器完成标准 OAuth 授权码 + PKCE 登录并获取 Token。"""

    def __init__(
        self,
        *,
        profile_dir: Optional[str] = None,
        headless: bool = False,
        timeout: int = 600,
        channel: Optional[str] = None,
        on_message: MessageFn = None,
    ):
        """channel：启动系统浏览器（"msedge" / "chrome" / "firefox"），None 使用 Playwright Chromium。"""
        self.profile_dir = Path(profile_dir) if profile_dir else None
        self.headless = headless
        self.timeout = timeout
        self.channel = channel
        self.on_message = on_message

    # ------------------------------------------------------------------
    def _say(self, msg: str) -> None:
        log.info(msg)
        if self.on_message:
            self.on_message(msg)

    def capture(self) -> TokenBundle:
        """标准 OAuth 授权码 + PKCE 流程：登录后返回 access + refresh。"""
        try:
            from playwright.sync_api import sync_playwright
        except ImportError as exc:
            raise AuthError(
                "未安装 playwright。请先执行：pip install playwright && playwright install chromium"
            ) from exc

        # 生成 PKCE 参数与 state / nonce
        verifier = oauth.generate_code_verifier()
        challenge = oauth.compute_code_challenge(verifier)
        state = secrets.token_urlsafe(16)
        nonce = secrets.token_urlsafe(16)
        auth_url = oauth.build_authorize_url(
            state=state,
            code_challenge=challenge,
            nonce=nonce,
            authorize_url=oauth.AUTHORIZE_URL,
            client_id=oauth.CLIENT_ID,
            redirect_uri=oauth.REDIRECT_URI,
            scope=oauth.SCOPE,
        )

        profile = self.profile_dir or Path.home() / ".forza-sync" / "browser_profile"
        captured: dict = {}

        with sync_playwright() as p:
            browser_type = "chromium"
            launch_kwargs: dict = dict(
                user_data_dir=str(profile),
                headless=self.headless,
                no_viewport=True,
                # 移除 Playwright 默认注入的 --no-sandbox，避免 Edge/Chrome 报
                # “不支持的命令行参数”警告（Chromium 沙箱在 Windows 默认启用）
                ignore_default_args=["--no-sandbox"],
            )
            if self.channel in ("msedge", "chrome"):
                # 系统 Chromium 系浏览器（Edge/Chrome）
                browser_type = "chromium"
                launch_kwargs["channel"] = self.channel
                launch_kwargs["args"] = ["--start-maximized"]
            elif self.channel == "firefox":
                # 系统 Firefox：Playwright 的 Firefox 通道需指定可执行文件
                browser_type = "firefox"
                fp = _find_firefox()
                if not fp:
                    raise AuthError("未找到系统 Firefox，请确认已安装后重试")
                launch_kwargs["executable_path"] = str(fp)
            else:
                # Playwright 自带 Chromium
                browser_type = "chromium"
                launch_kwargs["args"] = ["--start-maximized"]

            try:
                if browser_type == "firefox":
                    context = p.firefox.launch_persistent_context(**launch_kwargs)
                else:
                    context = p.chromium.launch_persistent_context(**launch_kwargs)
            except Exception as exc:
                hint = "请确认系统已安装该浏览器" if self.channel else "请先执行 playwright install chromium"
                raise AuthError(f"无法启动浏览器（{self.channel or 'chromium'}）：{exc}\n{hint}") from exc

            page = context.pages[0] if context.pages else context.new_page()

            def _capture_code(url: str) -> bool:
                """从 URL 提取并校验授权码；成功则记录并返回 True。"""
                code = oauth.parse_redirect(url, state)
                if code and "code" not in captured:
                    captured["code"] = code
                    self._say("已获取授权码 ✅")
                    return True
                return False

            def _on_request(request) -> None:
                """在回调请求发出的第一时间捕获授权码，并阻止页面加载 forza.net。"""
                if not request.url.startswith(oauth.REDIRECT_URI):
                    return
                if _capture_code(request.url):
                    # 捕获后立即导航到空白页，避免加载 forza.net 回调页
                    # （SPA 可能抢先消费一次性授权码）
                    try:
                        page.goto("about:blank", wait_until="commit", timeout=5000)
                    except Exception:
                        pass

            def _on_frame_navigated(frame) -> None:
                """兜底：主框架导航时检查回调 URL（request 事件漏检时使用）。"""
                if frame != page.main_frame:
                    return
                try:
                    _capture_code(frame.url)
                except Exception:
                    pass

            page.on("request", _on_request)
            page.on("framenavigated", _on_frame_navigated)

            self._say(f"正在打开授权登录页…（最多 {self.timeout} 秒）")
            self._say("请在浏览器中登录你的 Xbox / Microsoft 账号（支持两步验证）")
            _goto(page, auth_url)

            deadline = time.time() + self.timeout
            while time.time() < deadline and "code" not in captured:
                time.sleep(1)

            context.close()

        if "code" not in captured:
            raise AuthError(
                "未在限定时间内完成登录（未获得授权码）。"
                "请重试，并确保在弹出的浏览器窗口中完成 Microsoft 账号登录。"
            )

        # 用授权码 + code_verifier 换取令牌
        self._say("正在交换授权码获取 Token…")
        bundle = oauth.exchange_code(
            code=captured["code"],
            code_verifier=verifier,
            client_id=oauth.CLIENT_ID,
            redirect_uri=oauth.REDIRECT_URI,
            token_url=oauth.TOKEN_URL,
        )
        if not bundle.refresh_token:
            self._say(
                "未获取到 refresh_token（自动续期不可用），将仅保存 access_token。"
                "可稍后运行 `forza-sync token refresh` 或手动配置 refresh_token。"
            )
        return bundle
