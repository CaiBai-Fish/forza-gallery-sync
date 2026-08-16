"""Token 刷新与自动刷新逻辑测试（不发起真实网络请求）。"""

import json
from datetime import datetime, timedelta, timezone

import pytest

from forza_sync.auth import TokenBundle, TokenManager, TokenRefresher
from forza_sync.config import Config, ConfigManager
from forza_sync.errors import AuthError, NetworkError


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


class TestTokenRefresher:
    def test_parses_response(self, monkeypatch):
        refresher = TokenRefresher()
        captured = {}

        def fake_post(url, data, timeout, verify):
            captured["data"] = data
            assert url == "https://api.forza.net/connect/token"
            return FakeResponse(
                200,
                {
                    "access_token": "NEW_ACCESS",
                    "refresh_token": "NEW_REFRESH",
                    "expires_in": 3299,
                },
            )

        monkeypatch.setattr(refresher.session, "post", fake_post)
        bundle = refresher.refresh("OLD_REFRESH")
        assert bundle.access_token == "NEW_ACCESS"
        assert bundle.refresh_token == "NEW_REFRESH"
        assert bundle.expires_in == 3299
        # 表单参数符合抓包结果
        assert captured["data"]["grant_type"] == "refresh_token"
        assert captured["data"]["client_id"] == "nuxt-spa"
        assert captured["data"]["scope"] == "openid profile offline_access"
        assert captured["data"]["refresh_token"] == "OLD_REFRESH"

    def test_keeps_old_refresh_when_missing(self, monkeypatch):
        refresher = TokenRefresher()

        def fake_post(*a, **k):
            return FakeResponse(200, {"access_token": "A", "expires_in": 100})

        monkeypatch.setattr(refresher.session, "post", fake_post)
        bundle = refresher.refresh("OLD")
        assert bundle.refresh_token == "OLD"

    def test_invalid_grant_raises_auth_error(self, monkeypatch):
        refresher = TokenRefresher()

        def fake_post(*a, **k):
            return FakeResponse(400, {})

        monkeypatch.setattr(refresher.session, "post", fake_post)
        with pytest.raises(AuthError):
            refresher.refresh("BAD")

    def test_missing_access_token_raises(self, monkeypatch):
        refresher = TokenRefresher()

        def fake_post(*a, **k):
            return FakeResponse(200, {"expires_in": 100})

        monkeypatch.setattr(refresher.session, "post", fake_post)
        with pytest.raises(AuthError):
            refresher.refresh("X")


class TestTokenManager:
    def _manager(self, tmp_path, cfg_dict=None):
        path = tmp_path / "config.json"
        cfg = Config.from_dict(cfg_dict or {})
        ConfigManager(path).save(cfg)
        return TokenManager(path), path

    def test_returns_existing_token(self, tmp_path):
        manager, _ = self._manager(tmp_path, {"token": "ABC"})
        assert manager.access_token() == "ABC"

    def test_refresh_saves_rotated_tokens(self, tmp_path, monkeypatch):
        manager, path = self._manager(tmp_path, {"token": "OLD", "refresh_token": "R"})

        def fake_refresh(self_, rt):
            return TokenBundle(access_token="NEW_ACCESS", refresh_token="NEW_RT", expires_in=3299)

        monkeypatch.setattr(TokenRefresher, "refresh", fake_refresh)
        assert manager.refresh() == "NEW_ACCESS"

        saved = ConfigManager(path).load()
        assert saved.token == "NEW_ACCESS"
        assert saved.refresh_token == "NEW_RT"
        assert saved.token_issued_at
        assert saved.token_expires_in == 3299

    def test_access_token_refreshes_when_expired(self, tmp_path, monkeypatch):
        issued = (datetime.now(timezone.utc) - timedelta(seconds=4000)).isoformat()
        manager, _ = self._manager(
            tmp_path,
            {"token": "OLD", "refresh_token": "R", "token_issued_at": issued,
             "token_expires_in": 3299},
        )

        def fake_refresh(self_, rt):
            return TokenBundle(access_token="NEW", refresh_token="R2", expires_in=3299)

        monkeypatch.setattr(TokenRefresher, "refresh", fake_refresh)
        assert manager.access_token() == "NEW"
        # 进程内缓存
        assert manager.access_token() == "NEW"

    def test_expired_without_refresh_token_returns_old(self, tmp_path):
        issued = (datetime.now(timezone.utc) - timedelta(seconds=4000)).isoformat()
        manager, _ = self._manager(
            tmp_path, {"token": "OLD", "token_issued_at": issued, "token_expires_in": 3299}
        )
        # 无 refresh_token：不预判刷新，返回现有 token（交给 401 处理）
        assert manager.access_token() == "OLD"

    def test_refresh_without_refresh_token_raises(self, tmp_path):
        manager, _ = self._manager(tmp_path, {"token": "ABC"})
        with pytest.raises(AuthError):
            manager.refresh()

    def test_status(self, tmp_path):
        issued = (datetime.now(timezone.utc) - timedelta(seconds=60)).isoformat()
        manager, _ = self._manager(
            tmp_path,
            {"token": "ABCDEFGHIJKLMNOP", "refresh_token": "R",
             "token_issued_at": issued, "token_expires_in": 3299},
        )
        info = manager.status()
        assert info["has_token"] is True
        assert info["has_refresh_token"] is True
        assert info["expired"] is False
        assert info["expires_in"] is not None and info["expires_in"] > 0
        assert "ABCD" in info["masked_token"]
        assert "OP" in info["masked_token"]


class TestApiClientAuthRetry:
    def test_on_auth_error_refreshes_and_retries(self, tmp_path, monkeypatch):
        from forza_sync.api_client import ForzaGalleryClient

        client = ForzaGalleryClient("OLD", on_auth_error=lambda: "NEW")
        calls = []

        def fake_request(method, url, timeout, verify, **kwargs):
            calls.append(url)
            if len(calls) == 1:
                return FakeResponse(401, {})
            return FakeResponse(200, {"results": [], "pagingInfo": {"totalRecords": 0}})

        monkeypatch.setattr(client.session, "request", fake_request)
        page = client.get_page("FH5")
        assert len(calls) == 2
        assert client.session.headers["Authorization"] == "Bearer NEW"
        assert page.total_records == 0

    def test_auth_error_without_callback_raises(self, monkeypatch):
        from forza_sync.api_client import ForzaGalleryClient

        client = ForzaGalleryClient("OLD")
        monkeypatch.setattr(
            client.session, "request",
            lambda *a, **k: FakeResponse(401, {}),
        )
        with pytest.raises(AuthError):
            client.get_page("FH5")
