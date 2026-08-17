"""OAuth 标准授权码流程测试（含真实 Playwright 端到端集成测试）。

覆盖：
- PKCE（code_verifier / code_challenge S256）
- 授权 URL 构造与回调解析（state 校验）
- 授权码交换（含错误处理）
- 完整流程：浏览器 → 授权 → 回调携带 code → 交换 token
"""

import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

import pytest

from forza_sync import oauth
from forza_sync.auth import TokenBundle
from forza_sync.errors import AuthError, NetworkError

try:
    import playwright  # noqa: F401

    _PLAYWRIGHT_AVAILABLE = True
except ImportError:
    _PLAYWRIGHT_AVAILABLE = False

# RFC 7636 附录 B 的官方测试向量
RFC_VERIFIER = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
RFC_CHALLENGE = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"


class TestPkce:
    def test_code_verifier_shape(self):
        for _ in range(20):
            v = oauth.generate_code_verifier()
            assert 43 <= len(v) <= 128
            assert all(c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_" for c in v)

    def test_code_challenge_rfc_vector(self):
        assert oauth.compute_code_challenge(RFC_VERIFIER) == RFC_CHALLENGE

    def test_code_challenge_deterministic(self):
        v = oauth.generate_code_verifier()
        assert oauth.compute_code_challenge(v) == oauth.compute_code_challenge(v)


class TestBuildAuthorizeUrl:
    def test_params(self):
        url = oauth.build_authorize_url(
            state="s1", code_challenge="ch1", nonce="n1",
            authorize_url="https://auth.example/authorize",
            client_id="cid", redirect_uri="https://app/cb", scope="openid profile",
        )
        qs = parse_qs(urlparse(url).query)
        assert qs["client_id"] == ["cid"]
        assert qs["redirect_uri"] == ["https://app/cb"]
        assert qs["response_type"] == ["code"]
        assert qs["scope"] == ["openid profile"]
        assert qs["state"] == ["s1"]
        assert qs["code_challenge"] == ["ch1"]
        assert qs["code_challenge_method"] == ["S256"]
        assert qs["nonce"] == ["n1"]

    def test_nonce_optional(self):
        url = oauth.build_authorize_url(state="s", code_challenge="c")
        assert "nonce" not in urlparse(url).query


class TestParseRedirect:
    def test_valid(self):
        assert oauth.parse_redirect("https://app/cb?code=ABC&state=xyz", "xyz") == "ABC"

    def test_wrong_state_returns_none(self):
        assert oauth.parse_redirect("https://app/cb?code=ABC&state=other", "xyz") is None

    def test_no_code_returns_none(self):
        assert oauth.parse_redirect("https://app/cb?state=xyz", "xyz") is None
        assert oauth.parse_redirect("https://login.live.com/...", "xyz") is None

    def test_extra_params(self):
        assert oauth.parse_redirect(
            "https://app/cb?code=ABC&state=xyz&session_state=a&iss=b", "xyz"
        ) == "ABC"


class FakeResponse:
    def __init__(self, status_code=200, json_data=None):
        self.status_code = status_code
        self._json = json_data
        self.headers = {}

    def json(self):
        if self._json is None:
            raise ValueError("no json")
        return self._json

    def raise_for_status(self):
        if self.status_code >= 400:
            import requests

            raise requests.HTTPError(f"{self.status_code}")


class TestExchangeCode:
    def test_exchange_ok(self, monkeypatch):
        captured = {}

        def fake_post(url, data, headers, timeout, verify):
            captured["url"] = url
            captured["data"] = data
            return FakeResponse(
                200,
                {"access_token": "ACC", "refresh_token": "REF", "expires_in": 3299},
            )

        monkeypatch.setattr(oauth.requests, "post", fake_post)
        bundle = oauth.exchange_code(code="C", code_verifier="V")
        assert bundle == TokenBundle("ACC", "REF", 3299)
        assert captured["data"]["grant_type"] == "authorization_code"
        assert captured["data"]["code"] == "C"
        assert captured["data"]["code_verifier"] == "V"
        assert captured["data"]["redirect_uri"] == oauth.REDIRECT_URI
        assert captured["data"]["client_id"] == "nuxt-spa"
        # OpenIddict ID2074：authorization_code 交换不允许携带 scope
        assert "scope" not in captured["data"]

    def test_invalid_grant_raises(self, monkeypatch):
        monkeypatch.setattr(
            oauth.requests, "post", lambda *a, **k: FakeResponse(400, {})
        )
        with pytest.raises(AuthError):
            oauth.exchange_code(code="C", code_verifier="V")

    def test_missing_access_token_raises(self, monkeypatch):
        monkeypatch.setattr(
            oauth.requests, "post",
            lambda *a, **k: FakeResponse(200, {"refresh_token": "R"}),
        )
        with pytest.raises(AuthError):
            oauth.exchange_code(code="C", code_verifier="V")

    def test_network_error_retries_then_raises(self, monkeypatch):
        import requests

        calls = {"n": 0}

        def boom(*a, **k):
            calls["n"] += 1
            raise requests.ConnectionError("reset")

        monkeypatch.setattr(oauth.requests, "post", boom)
        with pytest.raises(NetworkError):
            oauth.exchange_code(code="C", code_verifier="V", retries=3)
        assert calls["n"] == 3


class _OAuthServer:
    """本地模拟 OAuth 服务器：/authorize 重定向到 /callback 携带 code+state，/token 返回令牌。"""

    def __init__(self):
        self._httpd = None
        self._thread = None
        self.token_form = None

    def __enter__(self):
        server = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path.startswith("/authorize"):
                    state = parse_qs(urlparse(self.path).query).get("state", [""])[0]
                    base = f"http://{self.server.server_address[0]}:{self.server.server_address[1]}"
                    body = (
                        f"<html><body>login</body><script>"
                        f"location.replace('{base}/callback?code=TESTCODE&state={state}')"
                        f"</script></html>"
                    ).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "text/html")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                else:  # /callback
                    body = b"<html><body>callback ok</body></html>"
                    self.send_response(200)
                    self.send_header("Content-Type", "text/html")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)

            def do_POST(self):
                if self.path == "/token":
                    length = int(self.headers.get("Content-Length", 0))
                    server.token_form = parse_qs(
                        self.rfile.read(length).decode("utf-8")
                    )
                    body = json.dumps(
                        {"access_token": "ACC_TEST", "refresh_token": "REF_TEST", "expires_in": 3299}
                    ).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)

            def log_message(self, *args):
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
def test_full_oauth_flow_with_real_playwright(tmp_path, monkeypatch):
    """端到端验证标准 OAuth 流程：浏览器授权 → 捕获 code → 交换 token。"""
    from forza_sync.login import BrowserLogin

    with _OAuthServer() as server:
        base = server.base_url
        monkeypatch.setattr(oauth, "AUTHORIZE_URL", base + "/authorize")
        monkeypatch.setattr(oauth, "TOKEN_URL", base + "/token")
        monkeypatch.setattr(oauth, "REDIRECT_URI", base + "/callback")

        login = BrowserLogin(profile_dir=str(tmp_path / "profile"), headless=True, timeout=30)
        bundle = login.capture()

        assert bundle.access_token == "ACC_TEST"
        assert bundle.refresh_token == "REF_TEST"
        assert bundle.expires_in == 3299
        # 令牌交换请求符合标准授权码流程
        assert server.token_form["grant_type"] == ["authorization_code"]
        assert server.token_form["code"] == ["TESTCODE"]
        assert server.token_form["redirect_uri"] == [base + "/callback"]
        assert server.token_form["client_id"] == ["nuxt-spa"]
        assert "code_verifier" in server.token_form
