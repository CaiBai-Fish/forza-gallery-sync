"""浏览器自动登录任意 Xbox 账号并捕获 Token（基于 Playwright）。

流程：
1. 启动浏览器（有头模式，支持持久化会话），直接打开画廊页 https://forza.net/myforza
2. 若未登录，用户在浏览器窗口中登录任意 Xbox / Microsoft 账号（支持两步验证）
3. 工具自动捕获 token，两条路径：
   a) 拦截发往 api.forza.net 的**请求头**中的 `Authorization: Bearer ...`（access_token）
      —— 即使已登录、不再走 /connect/token，只要 SPA 调用画廊接口即可捕获
   b) 拦截 /connect/token 的**响应体**（access_token + refresh_token，用于自动续期）
4. 若只拿到 access_token，则从浏览器 localStorage / Cookie 中兜底提取 refresh_token
5. 保存到配置，之后由 auth.TokenManager 自动刷新续期

依赖（可选，仅在 login 命令需要）：
    pip install playwright && playwright install chromium
"""

from __future__ import annotations

import json
import logging
import os
import time
from pathlib import Path
from typing import Callable, List, Optional

from .auth import TokenBundle
from .errors import AuthError

log = logging.getLogger(__name__)

LOGIN_URL = "https://forza.net/"
GALLERY_PAGE_URL = "https://forza.net/myforza"  # 画廊页，进入后才触发画廊 API 请求
TOKEN_ENDPOINT_HINT = "/connect/token"
GALLERY_API_HINT = "/api/v4/me/gallery/"

# 周期性地重新进入画廊页以触发 API 请求（秒）
REENTER_INTERVAL = 8

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


def parse_token_json(data) -> Optional[TokenBundle]:
    """从 /connect/token 的 JSON 响应中提取 TokenBundle。

    结构符合 { access_token, refresh_token, expires_in } 时返回，
    否则返回 None（用于过滤无关响应）。
    """
    if not isinstance(data, dict):
        return None
    access = data.get("access_token")
    if not isinstance(access, str) or not access:
        return None
    refresh = data.get("refresh_token")
    if not isinstance(refresh, str) or not refresh:
        refresh = ""
    try:
        expires = int(data.get("expires_in") or 0)
    except (TypeError, ValueError):
        expires = 0
    return TokenBundle(access_token=access, refresh_token=refresh, expires_in=expires)


class BrowserLogin:
    """Playwright 浏览器登录与 Token 捕获。"""

    def __init__(
        self,
        *,
        profile_dir: Optional[str] = None,
        headless: bool = False,
        timeout: int = 600,
        channel: Optional[str] = None,
        on_message: MessageFn = None,
    ):
        """channel：启动系统浏览器（"msedge" / "chrome"），None 使用 Playwright 自带 Chromium。"""
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
        """启动浏览器并等待登录，返回捕获到的 TokenBundle。

        - 优先返回含 refresh_token 的完整 bundle（来自 /connect/token 响应或存储）
        - 否则返回仅有 access_token 的 bundle（来自 API 请求头），refresh_token 为空
        """
        try:
            from playwright.sync_api import sync_playwright
        except ImportError as exc:
            raise AuthError(
                "未安装 playwright。请先执行：pip install playwright && playwright install chromium"
            ) from exc

        profile = self.profile_dir or Path.home() / ".forza-sync" / "browser_profile"
        captured_access: List[str] = []
        captured_bundles: List[TokenBundle] = []

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

            def _on_request(request) -> None:
                """从发往 api.forza.net 的请求头中提取 access_token。"""
                url = request.url
                if "api.forza.net" not in url and GALLERY_API_HINT not in url:
                    return
                auth = request.headers.get("authorization") or ""
                if auth.lower().startswith("bearer "):
                    token = auth[7:].strip()
                    if token and token not in captured_access:
                        captured_access.append(token)
                        self._say("已从 API 请求头捕获到 access_token ✅")

            def _on_response(response) -> None:
                """从 /connect/token 响应体中提取 access + refresh。"""
                if TOKEN_ENDPOINT_HINT not in response.url:
                    return
                try:
                    data = response.json()
                except Exception:
                    try:
                        data = json.loads(response.text())
                    except Exception:
                        return
                bundle = parse_token_json(data)
                if bundle:
                    captured_bundles.append(bundle)
                    self._say("已捕获到完整 Token（含 refresh_token）✅")

            page.on("request", _on_request)
            page.on("response", _on_response)

            # 直接打开画廊页，进入后 SPA 会立即触发画廊 API 请求
            self._say(f"打开画廊页 {GALLERY_PAGE_URL} 等待登录…（最多 {self.timeout} 秒）")
            self._say("请在浏览器中登录你的 Xbox / Microsoft 账号（支持两步验证）")
            page.goto(GALLERY_PAGE_URL, wait_until="domcontentloaded")

            def _on_forza_origin() -> bool:
                """当前页面是否还在 forza.net 域（登录页 login.live.com 期间不打扰）。"""
                try:
                    return page.url.startswith("https://forza.net")
                except Exception:
                    return False

            deadline = time.time() + self.timeout
            last_enter = time.time()
            while time.time() < deadline and not captured_access and not captured_bundles:
                # 在 forza.net 域且一段时间无捕获时，重新进入画廊页触发请求
                if _on_forza_origin() and time.time() - last_enter >= REENTER_INTERVAL:
                    last_enter = time.time()
                    self._say("重新进入画廊页以触发请求…")
                    _goto(page, GALLERY_PAGE_URL)
                time.sleep(1)

            # 关闭前先尝试从浏览器存储中兜底提取（关闭后无法访问）
            storage = self._extract_from_storage(page, context)

            context.close()

        if captured_bundles:
            return captured_bundles[-1]

        if captured_access:
            access = captured_access[-1]
            refresh = storage.refresh_token if storage else ""
            bundle = TokenBundle(access_token=access, refresh_token=refresh, expires_in=0)
            if not refresh:
                self._say(
                    "仅捕获到 access_token，未获取到 refresh_token（自动续期不可用）。"
                    "如需自动续期，可在浏览器中触发一次刷新（产生 /connect/token 请求）"
                    "或手动配置 refresh_token。"
                )
            return bundle

        raise AuthError(
            "在限定时间内未捕获到 Token。请确认已成功登录，并在浏览器中访问『我的画廊』"
            "或刷新页面以触发 API 请求后重试。"
        )

    # ------------------------------------------------------------------
    def _extract_from_storage(self, page, context) -> Optional[TokenBundle]:
        """从浏览器 localStorage 与 Cookie 中兜底提取 token。"""
        bundle = self._extract_local_storage(page)
        if bundle:
            return bundle
        return self._extract_cookies(context)

    @staticmethod
    def _extract_local_storage(page) -> Optional[TokenBundle]:
        try:
            data = page.evaluate(
                """() => {
                    const out = {};
                    for (let i = 0; i < localStorage.length; i++) {
                        const k = localStorage.key(i);
                        out[k] = localStorage.getItem(k);
                    }
                    return out;
                }"""
            )
        except Exception:
            return None

        access: Optional[str] = None
        refresh: Optional[str] = None
        for key, value in (data or {}).items():
            if not isinstance(value, str) or not value:
                continue
            low = key.lower()
            if not (("token" in low) or ("auth" in low) or ("refresh" in low)):
                continue
            parsed = None
            try:
                parsed = json.loads(value)
            except Exception:
                parsed = None

            if isinstance(parsed, dict):
                for k in ("access_token", "accessToken", "AccessToken"):
                    if isinstance(parsed.get(k), str) and parsed[k]:
                        access = parsed[k]
                        break
                for k in ("refresh_token", "refreshToken", "RefreshToken"):
                    if isinstance(parsed.get(k), str) and parsed[k]:
                        refresh = parsed[k]
                        break
            else:
                # 裸字符串：键名含 refresh 且长度像 token 时视为 refresh_token
                if "refresh" in low and refresh is None and len(value) > 20:
                    refresh = value
            if access and refresh:
                break

        if not access:
            return None
        return TokenBundle(access_token=access, refresh_token=refresh or "", expires_in=0)

    @staticmethod
    def _extract_cookies(context) -> Optional[TokenBundle]:
        try:
            cookies = context.cookies("https://api.forza.net")
        except Exception:
            return None
        access: Optional[str] = None
        refresh: Optional[str] = None
        for c in cookies:
            name = (c.get("name") or "").lower()
            value = c.get("value") or ""
            if not value:
                continue
            if "refresh" in name and refresh is None:
                refresh = value
            elif ("access" in name or "token" in name) and access is None:
                access = value
        if not access:
            return None
        return TokenBundle(access_token=access, refresh_token=refresh or "", expires_in=0)
