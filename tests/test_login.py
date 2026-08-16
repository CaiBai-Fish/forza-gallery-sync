"""浏览器登录模块测试（含真实 Playwright 拦截集成测试）。"""

import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest

from forza_sync.auth import TokenBundle
from forza_sync.login import parse_token_json

try:
    import playwright  # noqa: F401

    _PLAYWRIGHT_AVAILABLE = True
except ImportError:
    _PLAYWRIGHT_AVAILABLE = False


class TestParseTokenJson:
    def test_valid_response(self):
        data = {
            "access_token": "ACC",
            "token_type": "Bearer",
            "expires_in": 3299,
            "refresh_token": "REF",
            "id_token": "eyJ...",
        }
        bundle = parse_token_json(data)
        assert bundle == TokenBundle(access_token="ACC", refresh_token="REF", expires_in=3299)

    def test_missing_refresh_returns_empty(self):
        bundle = parse_token_json({"access_token": "A", "expires_in": 100})
        assert bundle is not None
        assert bundle.refresh_token == ""
        assert bundle.expires_in == 100

    def test_non_dict_returns_none(self):
        assert parse_token_json([]) is None
        assert parse_token_json("str") is None
        assert parse_token_json(None) is None

    def test_missing_access_returns_none(self):
        assert parse_token_json({"refresh_token": "R"}) is None
        assert parse_token_json({}) is None

    def test_bad_expires_in_defaults_zero(self):
        bundle = parse_token_json({"access_token": "A", "expires_in": "abc"})
        assert bundle.expires_in == 0


class _TokenServer:
    """本地模拟服务器。

    mode="token"   ：首页脚本触发 /connect/token 请求（验证响应体捕获）
    mode="gallery" ：首页脚本以 Authorization 头请求 /api/v4/me/gallery/FH6（验证请求头捕获）
    """

    def __init__(self, token_data, mode="token"):
        self.token_data = token_data
        self.mode = mode
        self._httpd = None
        self._thread = None

    def __enter__(self):
        mode = self.mode
        token_data = self.token_data

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == "/connect/token":
                    body = json.dumps(token_data).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                elif self.path == "/api/v4/me/gallery/FH6":
                    body = json.dumps({"results": [], "pagingInfo": {"totalRecords": 0}}).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                else:
                    if mode == "gallery":
                        script = (
                            "fetch('/api/v4/me/gallery/FH6', "
                            "{headers: {'Authorization': 'Bearer ACC_HEADER_TEST'}})"
                        )
                    else:
                        script = "fetch('/connect/token')"
                    body = (
                        "<html><body>login</body><script>" + script + "</script></html>"
                    ).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "text/html")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)

            def log_message(self, *args):  # 静默日志
                pass

        self._httpd = HTTPServer(("127.0.0.1", 0), Handler)
        self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)
        self._thread.start()
        return self

    @property
    def base_url(self):
        host, port = self._httpd.server_address[:2]
        return f"http://{host}:{port}"

    def __exit__(self, *exc_info):
        self._httpd.shutdown()
        self._httpd.server_close()


@pytest.mark.skipif(not _PLAYWRIGHT_AVAILABLE, reason="playwright 未安装")
def test_capture_with_real_playwright(tmp_path, monkeypatch):
    """用真实 Playwright 验证：页面触发 /connect/token 后能捕获完整 token。"""
    from forza_sync.login import BrowserLogin

    token_data = {
        "access_token": "ACC_TEST",
        "refresh_token": "REF_TEST",
        "expires_in": 3299,
    }
    with _TokenServer(token_data, mode="token") as server:
        monkeypatch.setattr("forza_sync.login.LOGIN_URL", server.base_url + "/")
        monkeypatch.setattr("forza_sync.login.GALLERY_PAGE_URL", server.base_url + "/")
        login = BrowserLogin(
            profile_dir=str(tmp_path / "profile"), headless=True, timeout=30
        )
        bundle = login.capture()
        assert bundle.access_token == "ACC_TEST"
        assert bundle.refresh_token == "REF_TEST"
        assert bundle.expires_in == 3299


@pytest.mark.skipif(not _PLAYWRIGHT_AVAILABLE, reason="playwright 未安装")
def test_capture_access_from_request_header(tmp_path, monkeypatch):
    """用真实 Playwright 验证：从画廊 API 请求头中捕获 access_token。"""
    from forza_sync.login import BrowserLogin

    with _TokenServer({}, mode="gallery") as server:
        monkeypatch.setattr("forza_sync.login.LOGIN_URL", server.base_url + "/")
        monkeypatch.setattr("forza_sync.login.GALLERY_PAGE_URL", server.base_url + "/")
        login = BrowserLogin(
            profile_dir=str(tmp_path / "profile"), headless=True, timeout=30
        )
        bundle = login.capture()
        assert bundle.access_token == "ACC_HEADER_TEST"
        # 未捕获到 refresh_token（该路径只拿到 access）
        assert bundle.refresh_token == ""
