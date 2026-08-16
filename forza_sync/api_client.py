"""Forza Gallery API 客户端。

端点：GET https://api.forza.net/api/v4/me/gallery/{GAME}
认证：Authorization: Bearer <token>

分页：API 未公开确切的分页参数，本模块实现自适应探测，
自动尝试 page/pageSize、skip/take、offset/limit、pageNumber/pageSize
四种方案，并缓存到配置，从而支持超过一页的数据。
"""

from __future__ import annotations

import logging
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Optional

import requests

from .errors import ApiError, AuthError, DataFormatError, NetworkError

log = logging.getLogger(__name__)

BASE_URL = "https://api.forza.net/api/v4"

# 候选分页参数组合：方案名 -> {页码/偏移参数名, 每页数量参数名}
PAGINATION_SCHEMES: dict[str, dict[str, str]] = {
    "page": {"offset": "page", "page_size": "pageSize"},
    "skip": {"offset": "skip", "page_size": "take"},
    "offset": {"offset": "offset", "page_size": "limit"},
    "page_number": {"offset": "pageNumber", "page_size": "pageSize"},
}

# 照片记录期望的字段（photoCdnPath 为下载原图所必需）
REQUIRED_PHOTO_FIELD = "photoCdnPath"
OPTIONAL_PHOTO_FIELDS = (
    "title",
    "description",
    "submissionTimeUtc",
    "thumbnailCdnPath",
    "previewCdnPath",
)

# 分页探测阶段允许吞掉并继续尝试下一方案的异常
PROBE_RETRYABLE_EXCEPTIONS = (ApiError, NetworkError, AuthError, DataFormatError)


@dataclass
class Photo:
    """一张照片的元数据。"""

    game: str
    title: str
    description: Optional[str]
    submission_time_utc: str
    photo_url: str
    thumbnail_url: Optional[str]
    preview_url: Optional[str]
    raw: dict = field(default_factory=dict, repr=False)

    @classmethod
    def from_api(cls, item: Any, game: str) -> "Photo":
        """从 API 返回的单条记录构造 Photo。

        结构不符合预期时抛出 :class:`DataFormatError`，由上层决定跳过或终止。
        """
        if not isinstance(item, dict):
            raise DataFormatError(f"照片记录不是 JSON 对象: {item!r}")
        photo_url = item.get(REQUIRED_PHOTO_FIELD)
        if not isinstance(photo_url, str) or not photo_url.strip():
            raise DataFormatError(f"照片记录缺少 {REQUIRED_PHOTO_FIELD}: {item!r}")
        return cls(
            game=game,
            title=_as_str(item.get("title")),
            description=_as_optional_str(item.get("description")),
            submission_time_utc=_as_optional_str(item.get("submissionTimeUtc")) or "",
            photo_url=photo_url,
            thumbnail_url=_as_optional_str(item.get("thumbnailCdnPath")),
            preview_url=_as_optional_str(item.get("previewCdnPath")),
            raw=item,
        )


@dataclass
class GalleryPage:
    """一页照片列表。"""

    results: list
    total_records: int


def _as_optional_str(value: Any) -> Optional[str]:
    if value is None:
        return None
    if isinstance(value, str):
        return value
    return str(value)


def _as_str(value: Any) -> str:
    """可选文本字段：缺失时返回空字符串（如 title）。"""
    if value is None:
        return ""
    return str(value)


class ForzaGalleryClient:
    """Forza Gallery API 客户端。"""

    def __init__(
        self,
        token: str,
        *,
        base_url: str = BASE_URL,
        timeout: int = 30,
        retries: int = 3,
        verify_ssl: bool = True,
        user_agent: str = "forza-sync",
        on_auth_error: Optional[Callable[[], Optional[str]]] = None,
    ):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self.retries = max(1, retries)
        self.verify_ssl = verify_ssl
        self._probed_scheme: Optional[str] = None
        # 401 时调用，返回新的 access token（用于自动刷新后重试一次）
        self._on_auth_error = on_auth_error

        self.session = requests.Session()
        self.session.headers.update(
            {
                "Authorization": f"Bearer {token}",
                "Accept": "application/json",
                "User-Agent": user_agent,
            }
        )

    def set_token(self, token: str) -> None:
        """更新会话中的 Bearer token。"""
        self.session.headers["Authorization"] = f"Bearer {token}"

    # ------------------------------------------------------------------
    # HTTP 层
    # ------------------------------------------------------------------
    def _request(self, method: str, url: str, **kwargs) -> requests.Response:
        """带鉴权与重试的请求。

        - 401/403 -> AuthError（Token 过期/无效）
        - 429 / 5xx -> 按指数退避重试
        - 网络异常 -> 按指数退避重试
        - 其他 4xx -> ApiError
        """
        last_exc: Optional[Exception] = None
        refreshed = False  # 同一请求最多自动刷新一次，避免死循环
        for attempt in range(1, self.retries + 1):
            try:
                resp = self.session.request(
                    method, url, timeout=self.timeout, verify=self.verify_ssl, **kwargs
                )
            except requests.exceptions.SSLError as exc:
                raise ApiError(f"SSL 校验失败（可尝试将 verify_ssl 设为 false）: {exc}") from exc
            except requests.exceptions.RequestException as exc:
                last_exc = NetworkError(f"网络异常: {exc}")
                log.warning("网络异常（第 %d/%d 次重试）: %s", attempt, self.retries, exc)
                _backoff(attempt)
                continue

            if resp.status_code in (401, 403):
                if not refreshed and self._on_auth_error is not None:
                    new_token = self._on_auth_error()
                    if new_token:
                        refreshed = True
                        self.set_token(new_token)
                        log.info("Token 已自动刷新，重试请求")
                        continue
                raise AuthError(
                    "Token 已过期或无效，且自动刷新失败。请重新运行 `forza-sync config` 更新 Token"
                )

            if resp.status_code == 429 or resp.status_code >= 500:
                retry_after = _parse_retry_after(resp.headers.get("Retry-After"))
                if attempt < self.retries:
                    log.warning(
                        "服务器返回 %s（第 %d/%d 次重试）", resp.status_code, attempt, self.retries
                    )
                    time.sleep(retry_after if retry_after else min(2**attempt, 10))
                    continue
                resp.raise_for_status()

            resp.raise_for_status()
            return resp

        raise last_exc if last_exc else ApiError("请求失败")

    # ------------------------------------------------------------------
    # 数据解析
    # ------------------------------------------------------------------
    def _parse_gallery(self, data: Any, game: str) -> GalleryPage:
        """解析画廊响应，校验结构（格式变化防护）。"""
        if not isinstance(data, dict):
            raise DataFormatError(f"[{game}] API 返回的不是 JSON 对象: {type(data).__name__}")
        results_raw = data.get("results")
        if not isinstance(results_raw, list):
            raise DataFormatError(f"[{game}] API 返回缺少 results 数组")

        paging = data.get("pagingInfo")
        total = None
        if isinstance(paging, dict):
            try:
                total = int(paging.get("totalRecords"))
            except (TypeError, ValueError):
                total = None
        if total is None:
            total = len(results_raw)

        photos: list = []
        for item in results_raw:
            try:
                photos.append(Photo.from_api(item, game))
            except DataFormatError as exc:
                # 单条坏数据跳过，不中断整体同步
                log.warning("[%s] 跳过无法解析的照片记录: %s", game, exc)
        return GalleryPage(results=photos, total_records=total)

    # ------------------------------------------------------------------
    # 分页
    # ------------------------------------------------------------------
    @staticmethod
    def _build_params(scheme: str, *, page_num: int, page_size: int) -> dict[str, int]:
        """根据分页方案构建查询参数。

        page/pageNumber 类：offset 参数为 1 起始页码
        skip/offset 类：offset 参数为 0 起始跳过数量
        """
        if scheme == "page":
            return {"page": page_num, "pageSize": page_size}
        if scheme == "page_number":
            return {"pageNumber": page_num, "pageSize": page_size}
        if scheme == "skip":
            return {"skip": page_num, "take": page_size}
        if scheme == "offset":
            return {"offset": page_num, "limit": page_size}
        return {"page": page_num, "pageSize": page_size}

    def get_page(
        self,
        game: str,
        *,
        scheme: Optional[str] = None,
        page_num: int = 1,
        page_size: int = 50,
    ) -> GalleryPage:
        """获取单页照片列表。

        scheme 为 None 时不带分页参数（用于探测基线）。
        """
        params: dict = {}
        if scheme:
            params = self._build_params(scheme, page_num=page_num, page_size=page_size)
        url = f"{self.base_url}/me/gallery/{game}"
        resp = self._request("GET", url, params=params)
        try:
            payload = resp.json()
        except ValueError as exc:
            raise DataFormatError(f"[{game}] API 返回的不是合法 JSON: {exc}") from exc
        return self._parse_gallery(payload, game)

    def resolve_scheme(self, game: str, scheme: str, page_size: int) -> Optional[str]:
        """确定使用的分页方案。scheme='auto' 时进行一次性探测。"""
        if scheme != "auto":
            return scheme
        if self._probed_scheme is not None:
            return self._probed_scheme
        self._probed_scheme = self._probe(game, page_size)
        return self._probed_scheme

    def _probe(self, game: str, page_size: int) -> str:
        """探测可用的分页方案（每个进程只做一次）。

        基线请求一页数据；若总数不超过单页，则无需分页。
        否则依次尝试候选方案取第 2 页，若第 2 页与第 1 页结果不重叠，则认为该方案可用。
        """
        log.info("[%s] 探测分页方式…", game)
        base = self.get_page(game)
        if base.total_records <= len(base.results) or not base.results:
            log.info("[%s] 照片数不超过单页，无需分页", game)
            return "none"

        base_urls = {p.photo_url for p in base.results}
        for scheme in PAGINATION_SCHEMES:
            try:
                if scheme in ("page", "page_number"):
                    page2 = self.get_page(game, scheme=scheme, page_num=2, page_size=page_size)
                else:
                    page2 = self.get_page(
                        game, scheme=scheme, page_num=len(base.results), page_size=page_size
                    )
            except PROBE_RETRYABLE_EXCEPTIONS as exc:
                log.warning("[%s] 分页方案 %s 探测失败: %s", game, scheme, exc)
                continue
            if page2.results:
                first = page2.results[0].photo_url
                if first not in base_urls:
                    log.info("[%s] 检测到可用分页方案: %s", game, scheme)
                    return scheme
        log.warning("[%s] 未能探测到分页方案，默认使用 page/pageSize", game)
        return "page"

    def fetch_all(
        self,
        game: str,
        *,
        scheme: str = "auto",
        page_size: int = 50,
        max_pages: Optional[int] = None,
    ) -> list:
        """拉取该游戏的全部照片（跨页去重）。"""
        effective = self.resolve_scheme(game, scheme, page_size)
        collected: list = []
        seen: set = set()

        if effective in ("page", "page_number"):
            page_num = 1
            while True:
                page = self.get_page(game, scheme=effective, page_num=page_num, page_size=page_size)
                _append_unique(collected, seen, page.results)
                total = page.total_records
                if not page.results or len(collected) >= total or (
                    max_pages and page_num >= max_pages
                ):
                    break
                page_num += 1
        elif effective == "none":
            page = self.get_page(game)
            _append_unique(collected, seen, page.results)
        else:  # skip / offset
            skip = 0
            while True:
                page = self.get_page(
                    game, scheme=effective, page_num=skip, page_size=page_size
                )
                _append_unique(collected, seen, page.results)
                total = page.total_records
                if not page.results or len(collected) >= total or (
                    max_pages and skip >= max_pages * page_size
                ):
                    break
                skip += len(page.results)

        log.info("[%s] 共拉取 %d 张照片", game, len(collected))
        return collected


def _append_unique(collected: list, seen: set, results: list) -> None:
    """按 photo_url 去重后追加到列表。"""
    for p in results:
        if p.photo_url not in seen:
            seen.add(p.photo_url)
            collected.append(p)


def _backoff(attempt: int) -> None:
    time.sleep(min(2**attempt, 10))


def _parse_retry_after(value: Optional[str]) -> Optional[float]:
    if not value:
        return None
    try:
        return max(0.0, float(value))
    except ValueError:
        return None
