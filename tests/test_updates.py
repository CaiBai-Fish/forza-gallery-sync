"""updates 模块测试：版本解析、CHANGELOG 解析与更新检查（不依赖真实网络与本地文件）。"""

import json

import pytest
import requests

from forza_sync.updates import (
    RELEASES_PAGE_URL,
    _changelog_text_from_blob_html,
    _is_release_tag,
    _normalize_tag,
    _parse_version,
    _version_key,
    check_update,
    parse_changelog,
)

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
        if self.status_code >= 400:
            raise requests.exceptions.HTTPError(f"HTTP {self.status_code}")

    def json(self):
        return self._payload


def _patch(monkeypatch, handler):
    """把 requests.get 指到 handler(url, **kwargs)。"""
    monkeypatch.setattr("forza_sync.updates.requests.get", lambda url, **kw: handler(url, **kw))
    # 隔离本地 CHANGELOG，避免测试结果受工作目录影响
    monkeypatch.setattr("forza_sync.updates._find_changelog", lambda: None)


def _make_handler(
    *,
    releases,
    tags_api,
    latest,
    tags_page,
    changelog=CHANGELOG_SAMPLE,
    blob="",
):
    """按 5 路探测各路径的期望行为生成 handler。

    每一路可以是：``_Resp``（原样返回）、字符串（作为 200 正文）、
    ``None``（网络错误），或 ``403``（限流响应）。
    """

    def handler(url, **kw):
        if "api.github.com" in url and "/releases/latest" in url:
            return _as_resp(releases, url)
        if "api.github.com" in url and "/tags" in url:
            return _as_resp(tags_api, url)
        if "github.com" in url and "/releases/latest" in url:
            if latest is None:
                raise OSError("connection refused")
            if latest == 302:
                # 无 Release 时 GitHub 跳到 /releases（没有 tag 段）
                return _Resp(status_code=302, headers={"Location": "https://github.com/CaiBai-Fish/forza-gallery-sync/releases"})
            return _Resp(status_code=302, headers={"Location": latest})
        if "github.com" in url and url.rstrip("/").endswith("/tags"):
            return _as_resp(tags_page, url)
        if "github.com" in url and "/blob/" in url:
            return _as_resp(blob, url)
        # raw / CDN / contents API：CHANGELOG 正文
        return _as_resp(changelog, url)

    def _as_resp(value, url):
        if value is None:
            raise OSError("connection refused")
        if value == 403:
            return _Resp(status_code=403, headers={"X-RateLimit-Remaining": "0", "X-RateLimit-Limit": "60"})
        if isinstance(value, _Resp):
            return value
        return _Resp(text=value)

    return handler


def _blob_page(lines):
    """构造 github.com/blob 页面（内嵌 JSON 里带 rawLines）。"""
    payload = {
        "payload": {
            "codeViewBlobLayoutRoute": {
                "StyledBlob": {"rawLines": lines}
            }
        }
    }
    return (
        '<html><body><script type="application/json" data-target="react-app.embeddedData">'
        + json.dumps(payload)
        + "</script></body></html>"
    )


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


@pytest.mark.parametrize(
    ("route", "kwargs", "expected_version", "expected_source"),
    [
        # 路径 1：Releases API（最优先）
        (
            "releases-api",
            {"releases": _Resp(payload={"tag_name": "v1.2.0"}), "tags_api": "no", "latest": 302, "tags_page": "no"},
            "1.2.0",
            "releases-api",
        ),
        # 路径 2：Tags API（API 限流时）
        (
            "tags-api",
            {
                "releases": 403,
                "tags_api": _Resp(payload=[{"name": "1.2.0-abc1234"}, {"name": "1.1.0"}]),
                "latest": 302,
                "tags_page": "no",
            },
            "1.2.0",
            "tags-api",
        ),
        # 路径 3：/releases/latest 的 302 跳转（两个 API 都不可用时）
        (
            "releases-redirect",
            {
                "releases": None,
                "tags_api": None,
                "latest": "https://github.com/CaiBai-Fish/forza-gallery-sync/releases/tag/v1.2.0",
                "tags_page": "no",
            },
            "1.2.0",
            "releases-redirect",
        ),
        # 路径 4：/tags 页面 HTML（API 与 302 都失败时）
        (
            "tags-page",
            {
                "releases": None,
                "tags_api": None,
                "latest": 302,
                "tags_page": '<a href="/CaiBai-Fish/forza-gallery-sync/releases/tag/v1.2.0">1.2.0</a>',
            },
            "1.2.0",
            "tags-page",
        ),
        # 路径 5：CHANGELOG 保底（其余四路全失败）
        (
            "changelog",
            {"releases": None, "tags_api": None, "latest": None, "tags_page": None},
            "1.2.0",
            "changelog",
        ),
    ],
)
def test_probe_routes(monkeypatch, tmp_path, route, kwargs, expected_version, expected_source):
    """五路探测：任一路可用即可拿到版本号，且 source 如实标注命中的是哪一路。"""
    _patch(monkeypatch, _make_handler(**kwargs))
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")
    monkeypatch.delenv("FORZA_SYNC_CHANGELOG_URL", raising=False)

    res = check_update(config_path=str(tmp_path / "cfg.json"))

    assert res["error"] == "", f"{route} 应成功，实际错误：{res['error']}"
    assert res["latest"] == expected_version
    assert res["probe_source"] == expected_source
    assert res["has_update"] is True
    assert "新功能" in res["notes"]
    assert res["url"].startswith("https://github.com/")

    # attempts 要如实记录每一路的成功/失败（诊断用）
    ok_sources = [a["source"] for a in res["attempts"] if a["ok"]]
    failed_sources = [a["source"] for a in res["attempts"] if not a["ok"]]
    assert ok_sources == [expected_source]
    assert expected_source not in failed_sources


def test_probe_changelog_falls_back_to_blob_page(monkeypatch, tmp_path):
    """raw 与 CDN 都不通时，用 github.com 的 blob 页面（内嵌 JSON）取出 CHANGELOG。"""
    blob = _blob_page(CHANGELOG_SAMPLE.splitlines())

    def handler(url, **kw):
        if "raw.githubusercontent.com" in url or "jsdelivr" in url:
            raise OSError("unreachable")
        if "/blob/" in url:
            return _Resp(text=blob)
        if "api.github.com" in url:
            return _Resp(status_code=403, headers={"X-RateLimit-Remaining": "0"})
        if "/releases/latest" in url:
            raise OSError("unreachable")
        return _Resp(text="no tags here")

    _patch(monkeypatch, handler)
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")
    monkeypatch.delenv("FORZA_SYNC_CHANGELOG_URL", raising=False)

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["latest"] == "1.2.0"
    assert res["probe_source"] == "changelog"
    assert "新功能" in res["notes"]


def test_blob_html_extraction_handles_missing_data():
    """blob 页面结构变化时应返回空字符串，而不是抛异常。"""
    assert _changelog_text_from_blob_html("<html>nothing here</html>") == ""
    assert _changelog_text_from_blob_html("") == ""


def test_normalize_and_validate_tags():
    assert _normalize_tag("v1.2.3") == "1.2.3"
    assert _normalize_tag("1.2.3-abc1234") == "1.2.3"
    assert _is_release_tag("v1.0.0") is True
    assert _is_release_tag("1.0.2-abc1234") is True
    assert _is_release_tag("nightly") is False
    assert _is_release_tag("v1") is False


def test_check_update_up_to_date(monkeypatch, tmp_path):
    _patch(monkeypatch, _make_handler(
        releases=_Resp(payload={"tag_name": "v1.2.0"}), tags_api="no", latest=302, tags_page="no"
    ))
    monkeypatch.setattr("forza_sync.updates.__version__", "1.2.0")

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["has_update"] is False
    assert res["latest"] == "1.2.0"


def test_check_update_error_mentions_every_route(monkeypatch, tmp_path):
    """五路全失败时，error 里要带上每一路的具体原因，便于定位（403 / 超时 / 不可达要能区分）。"""

    def boom(url, **kw):
        raise OSError("network down")

    _patch(monkeypatch, boom)
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")
    monkeypatch.delenv("FORZA_SYNC_CHANGELOG_URL", raising=False)

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["has_update"] is False
    assert "无法获取最新版本" in res["error"]
    assert "network down" in res["error"]
    # 手动出口必须保留
    assert res["url"] == RELEASES_PAGE_URL
    # 五路都要出现在诊断里
    for name in ("releases API", "tags API", "releases/latest 跳转", "tags 页面", "CHANGELOG"):
        assert name in res["error"], f"{name} 的原因没有出现在 error 里"
    assert len(res["attempts"]) == 5
    assert all(a["ok"] is False for a in res["attempts"])


def test_check_update_rate_limit_message(monkeypatch, tmp_path):
    """API 限流时给出可读提示（含恢复时间），并继续走非 API 路径。"""
    _patch(monkeypatch, _make_handler(
        releases=403,
        tags_api=403,
        latest="https://github.com/CaiBai-Fish/forza-gallery-sync/releases/tag/v1.2.0",
        tags_page="no",
    ))
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")
    monkeypatch.delenv("FORZA_SYNC_CHANGELOG_URL", raising=False)

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    # 两条 API 路径被限流，但第三路（302 跳转）成功，所以整体仍能拿到版本
    assert res["probe_source"] == "releases-redirect"
    assert res["latest"] == "1.2.0"

    # 限流原因要留在 attempts/失败记录里，供排查"为什么走了非 API 路径"
    failures = "；".join(
        a.get("reason", "") for a in res["attempts"] if not a["ok"]
    )
    assert "匿名请求次数已用尽" in failures or "限流" in failures


def test_check_update_all_api_rate_limited_reports_actionable_error(monkeypatch, tmp_path):
    """五路全废且其中两路是限流时，错误提示要说清"额度用尽 + 何时恢复"。"""

    def handler(url, **kw):
        if "api.github.com" in url:
            return _Resp(
                status_code=403,
                headers={"X-RateLimit-Remaining": "0", "X-RateLimit-Limit": "60", "X-RateLimit-Reset": "4102444800"},
            )
        if "/releases/latest" in url or "/tags" in url or "/blob/" in url:
            raise OSError("blocked")
        return _Resp(text="没有版本号")

    _patch(monkeypatch, handler)
    monkeypatch.setattr("forza_sync.updates.__version__", "1.0.0")
    monkeypatch.delenv("FORZA_SYNC_CHANGELOG_URL", raising=False)

    res = check_update(config_path=str(tmp_path / "cfg.json"))
    assert res["has_update"] is False
    assert "匿名请求次数已用尽" in res["error"]
    assert "FORZA_SYNC_GITHUB_TOKEN" in res["error"]


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
