"""updates 模块测试：版本解析与更新检查（不依赖真实网络）。"""

from forza_sync.updates import _parse_version, check_update


def test_parse_version():
    assert _parse_version("0.4.0") == (0, 4, 0)
    assert _parse_version("v1.2.3") == (1, 2, 3)
    assert _parse_version("0.4.0-a1b2c3d") == (0, 4, 0)
    assert _parse_version("10.0.1") == (10, 0, 1)
    assert _parse_version("") == (0,)


def test_check_update_has_update(monkeypatch, tmp_path):
    class FakeResp:
        def raise_for_status(self):
            return None

        def json(self):
            return {
                "tag_name": "v0.5.0",
                "html_url": "https://github.com/CaiBai-Fish/forza-gallery-sync/releases/tag/v0.5.0",
                "name": "v0.5.0",
                "published_at": "2026-01-01T00:00:00Z",
            }

    monkeypatch.setattr("forza_sync.updates.requests.get", lambda *a, **k: FakeResp())
    monkeypatch.setattr("forza_sync.updates.__version__", "0.4.0")

    res = check_update(config_path=str(tmp_path / "no-such-config.json"))
    assert res["has_update"] is True
    assert res["current"] == "0.4.0"
    assert res["latest"] == "0.5.0"
    assert res["url"].startswith("https://github.com")
    assert res["error"] == ""


def test_check_update_up_to_date(monkeypatch, tmp_path):
    class FakeResp:
        def raise_for_status(self):
            return None

        def json(self):
            return {"tag_name": "v0.4.0", "html_url": "https://x", "name": "", "published_at": ""}

    monkeypatch.setattr("forza_sync.updates.requests.get", lambda *a, **k: FakeResp())
    monkeypatch.setattr("forza_sync.updates.__version__", "0.4.0")

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["has_update"] is False
    assert res["latest"] == "0.4.0"


def test_check_update_error(monkeypatch, tmp_path):
    def boom(*a, **k):
        raise OSError("network down")

    monkeypatch.setattr("forza_sync.updates.requests.get", boom)
    monkeypatch.setattr("forza_sync.updates.__version__", "0.4.0")

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["error"] != ""
    assert res["has_update"] is False


def test_check_update_sends_token(monkeypatch, tmp_path):
    """设置了 FORZA_SYNC_GITHUB_TOKEN 时应携带 Authorization 头。"""
    captured: dict = {}

    class FakeResp:
        def raise_for_status(self):
            return None

        def json(self):
            return {"tag_name": "v0.4.0", "html_url": "", "name": "", "published_at": ""}

    def fake_get(url, **kwargs):
        captured.update(kwargs)
        return FakeResp()

    monkeypatch.setattr("forza_sync.updates.requests.get", fake_get)
    monkeypatch.setattr("forza_sync.updates.__version__", "0.4.0")
    monkeypatch.setenv("FORZA_SYNC_GITHUB_TOKEN", "ghp_testtoken")

    check_update(config_path=str(tmp_path / "cfg.json"))
    assert captured.get("headers", {}).get("Authorization") == "Bearer ghp_testtoken"


def test_check_update_no_token(monkeypatch, tmp_path):
    """未设置 token 时不应携带 Authorization 头。"""
    captured: dict = {}

    class FakeResp:
        def raise_for_status(self):
            return None

        def json(self):
            return {"tag_name": "v0.4.0", "html_url": "", "name": "", "published_at": ""}

    def fake_get(url, **kwargs):
        captured.update(kwargs)
        return FakeResp()

    monkeypatch.setattr("forza_sync.updates.requests.get", fake_get)
    monkeypatch.setattr("forza_sync.updates.__version__", "0.4.0")
    monkeypatch.setattr("forza_sync.updates.GITHUB_TOKEN", "")
    monkeypatch.delenv("FORZA_SYNC_GITHUB_TOKEN", raising=False)
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)

    check_update(config_path=str(tmp_path / "cfg.json"))
    assert "Authorization" not in captured.get("headers", {})


def test_check_update_uses_hardcoded_token(monkeypatch, tmp_path):
    """代码内硬编码的 GITHUB_TOKEN 应作为 Bearer 头发送。"""
    captured: dict = {}

    class FakeResp:
        def raise_for_status(self):
            return None

        def json(self):
            return {"tag_name": "v0.4.0", "html_url": "", "name": "", "published_at": ""}

    def fake_get(url, **kwargs):
        captured.update(kwargs)
        return FakeResp()

    monkeypatch.setattr("forza_sync.updates.requests.get", fake_get)
    monkeypatch.setattr("forza_sync.updates.__version__", "0.4.0")
    monkeypatch.setattr("forza_sync.updates.GITHUB_TOKEN", "ghp_hardcoded")
    monkeypatch.delenv("FORZA_SYNC_GITHUB_TOKEN", raising=False)
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)

    check_update(config_path=str(tmp_path / "cfg.json"))
    assert captured.get("headers", {}).get("Authorization") == "Bearer ghp_hardcoded"
