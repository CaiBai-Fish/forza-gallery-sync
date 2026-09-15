"""updates 模块测试：版本解析、CHANGELOG 解析与更新检查（不依赖真实网络与本地文件）。"""

import pytest

from forza_sync.updates import _parse_version, _version_key, check_update, parse_changelog

CHANGELOG_SAMPLE = """# 更新日志

## [Unreleased]

- 未发布的改动

## [1.2.0] - 2026-03-01

### 变更
- **新功能**：支持某件事

## [1.0.0] - 2026-01-01

### 变更
- 首个正式版
"""


class _Resp:
    """最小可用的 requests.Response 替身（changelog 与 API 共用）。"""

    def __init__(self, *, text="", payload=None, status_code=200, headers=None, encoding="utf-8"):
        self.text = text
        self._payload = payload if payload is not None else {}
        self.status_code = status_code
        self.headers = headers or {}
        self.encoding = encoding

    def raise_for_status(self):
        return None

    def json(self):
        return self._payload


def _patch(monkeypatch, handler):
    """把 requests.get 指到 handler(url, **kwargs)。"""
    monkeypatch.setattr("forza_sync.updates.requests.get", lambda url, **kw: handler(url, **kw))
    # 隔离本地 CHANGELOG，避免测试结果受工作目录影响
    monkeypatch.setattr("forza_sync.updates._find_changelog", lambda: None)


def test_parse_version():
    assert _parse_version("0.4.0") == (0, 4, 0)
    assert _parse_version("v1.2.3") == (1, 2, 3)
    assert _parse_version("0.4.0-a1b2c3d") == (0, 4, 0)
    assert _parse_version("10.0.1") == (10, 0, 1)
    assert _parse_version("") == (0,)


def test_version_key_falls_back_to_zero():
    assert _version_key("") == (0,)
    assert _version_key("not-a-version") == (0,)


def test_parse_changelog_picks_latest_release_and_skips_unreleased():
    parsed = parse_changelog(CHANGELOG_SAMPLE)
    assert parsed["latest"] == "1.2.0"
    assert "Unreleased" not in parsed["versions"]
    assert parsed["versions"] == ["1.2.0", "1.0.0"]
    assert parsed["dates"]["1.2.0"] == "2026-03-01"
    assert "新功能" in parsed["sections"]["1.2.0"]
    assert "Unreleased" not in parsed["sections"]


def test_parse_changelog_without_releases():
    parsed = parse_changelog("## [Unreleased]\n\n- 只有未发布内容\n")
    assert parsed["latest"] == ""
    assert parsed["versions"] == []


def test_check_update_has_update_from_changelog(monkeypatch, tmp_path):
    """主路径：从 CHANGELOG 判断有新版本。"""
    _patch(monkeypatch, lambda url, **kw: _Resp(text=CHANGELOG_SAMPLE))
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")

    res = check_update(config_path=str(tmp_path / "no-such-config.json"))
    assert res["has_update"] is True
    assert res["current"] == "1.0.0"
    assert res["latest"] == "1.2.0"
    assert res["source"] == "changelog"
    assert "新功能" in res["notes"]
    assert res["error"] == ""


def test_check_update_up_to_date(monkeypatch, tmp_path):
    _patch(monkeypatch, lambda url, **kw: _Resp(text=CHANGELOG_SAMPLE))
    monkeypatch.setattr("forza_sync.updates.__version__", "1.2.0")

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["has_update"] is False
    assert res["latest"] == "1.2.0"


def test_check_update_falls_back_to_api(monkeypatch, tmp_path):
    """CHANGELOG 拿不到版本号时回退 GitHub Releases API。"""
    changelog_url = "raw.githubusercontent.com"
    api_url = "api.github.com"

    def handler(url, **kw):
        if changelog_url in url:
            return _Resp(text="没有任何版本标题的文本")
        assert api_url in url
        return _Resp(
            payload={
                "tag_name": "v2.0.0",
                "html_url": "https://github.com/x/y/releases/tag/v2.0.0",
                "name": "v2.0.0",
                "published_at": "2026-02-02T00:00:00Z",
                "body": "API 提供的说明",
            }
        )

    _patch(monkeypatch, handler)
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["source"] == "github-api"
    assert res["latest"] == "2.0.0"
    assert res["has_update"] is True
    assert res["notes"] == "API 提供的说明"
    assert res["error"] == ""


def test_check_update_error_mentions_fallback_reason(monkeypatch, tmp_path):
    """全部来源都失败时，error 里要带上降级原因，便于定位。"""

    def boom(url, **kw):
        raise OSError("network down")

    _patch(monkeypatch, boom)
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["has_update"] is False
    assert "network down" in res["error"]
    assert "已回退到 API" in res["error"]


def test_check_update_rate_limit_message(monkeypatch, tmp_path):
    """限流时给出可读提示，并说明为什么回退到 API。"""

    def handler(url, **kw):
        if "raw.githubusercontent.com" in url or "jsdelivr" in url:
            return _Resp(text="无版本号")
        return _Resp(status_code=403, headers={"X-RateLimit-Remaining": "0", "X-RateLimit-Limit": "60"})

    _patch(monkeypatch, handler)
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert "rate limit" in res["error"].lower() or "限流" in res["error"] or "匿名请求次数已用尽" in res["error"]
    assert "已回退到 API" in res["error"]


@pytest.mark.parametrize(
    ("token_attr", "env_name", "expected"),
    [
        ("ghp_hardcoded", None, "Bearer ghp_hardcoded"),
        ("", "FORZA_SYNC_GITHUB_TOKEN", "Bearer ghp_fromenv"),
    ],
)
def test_check_update_sends_token(monkeypatch, tmp_path, token_attr, env_name, expected):
    """代码内 token 与环境变量 token 都要作为 Bearer 头发给 API。"""
    captured: dict = {}

    def handler(url, **kw):
        if "api.github.com" in url:
            captured.update(kw)
            return _Resp(payload={"tag_name": "v1.0.0", "html_url": "", "name": "", "published_at": ""})
        return _Resp(text="无版本号")

    _patch(monkeypatch, handler)
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")
    monkeypatch.setattr("forza_sync.updates.GITHUB_TOKEN", token_attr)
    monkeypatch.delenv("FORZA_SYNC_GITHUB_TOKEN", raising=False)
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)
    if env_name:
        monkeypatch.setenv(env_name, "ghp_fromenv")

    check_update(config_path=str(tmp_path / "cfg.json"))
    assert captured.get("headers", {}).get("Authorization") == expected


def test_check_update_no_token(monkeypatch, tmp_path):
    """未配置 token 时不应携带 Authorization 头。"""
    captured: dict = {}

    def handler(url, **kw):
        if "api.github.com" in url:
            captured.update(kw)
            return _Resp(payload={"tag_name": "v1.0.0", "html_url": "", "name": "", "published_at": ""})
        return _Resp(text="无版本号")

    _patch(monkeypatch, handler)
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")
    monkeypatch.setattr("forza_sync.updates.GITHUB_TOKEN", "")
    monkeypatch.delenv("FORZA_SYNC_GITHUB_TOKEN", raising=False)
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)

    check_update(config_path=str(tmp_path / "cfg.json"))
    assert "Authorization" not in captured.get("headers", {})
