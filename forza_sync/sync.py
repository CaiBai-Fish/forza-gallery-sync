"""同步编排：拉取照片列表 → 增量下载 → 记录数据库。

- 增量：以 photo_id（从 URL 提取的唯一 ID）判断是否已下载
- 并发：多线程下载图片，数据库写入通过锁保护
- 容错：单张照片失败不影响整体，失败项汇总返回
"""

from __future__ import annotations

import logging
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional, Tuple

from .api_client import ForzaGalleryClient, Photo
from .auth import TokenManager
from .config import Config
from .database import PhotoDatabase
from .downloader import ImageDownloader
from .errors import AuthError, ForzaSyncError
from .naming import build_relative_path, extract_photo_id

log = logging.getLogger(__name__)


@dataclass
class SyncStats:
    """一次同步的统计结果。"""

    synced: int = 0  # 本次新下载
    skipped: int = 0  # 已存在跳过
    failed: int = 0  # 失败
    failed_items: List[Tuple[str, str]] = field(default_factory=list)  # (url, 原因)


class SyncService:
    """同步服务。"""

    def __init__(self, cfg: Config, db: PhotoDatabase, token_manager: Optional[TokenManager] = None):
        self.cfg = cfg
        self.db = db
        self.token_manager = token_manager
        self.download_dir: Path = cfg.effective_download_dir()

        def _auth_error_handler() -> Optional[str]:
            if self.token_manager is None:
                return None
            try:
                return self.token_manager.refresh()
            except AuthError as exc:
                log.error("自动刷新 Token 失败: %s", exc)
                return None

        self.client = ForzaGalleryClient(
            cfg.token,
            timeout=cfg.timeout,
            retries=cfg.retries,
            verify_ssl=cfg.verify_ssl,
            user_agent=cfg.user_agent,
            on_auth_error=_auth_error_handler,
        )
        self.downloader = ImageDownloader(
            timeout=cfg.timeout,
            retries=cfg.retries,
            verify_ssl=cfg.verify_ssl,
            user_agent=cfg.user_agent,
        )

    # ------------------------------------------------------------------
    # 单游戏同步
    # ------------------------------------------------------------------
    def sync_game(
        self,
        game: str,
        *,
        force: bool = False,
        max_photos: Optional[int] = None,
        progress_cb=None,
    ) -> SyncStats:
        """同步指定游戏的全部照片。

        :param progress_cb: 可选进度回调，签名 ``(game, done, total, stats)``，
            每完成一张（或每 10 张）调用一次，用于 Web 界面实时展示进度。
        """
        stats = SyncStats()

        # 确保 access token 有效（必要时自动刷新）
        if self.token_manager is not None:
            self.client.set_token(self.token_manager.access_token())

        photos = self.client.fetch_all(
            game,
            scheme=self.cfg.pagination,
            page_size=self.cfg.page_size,
        )
        if max_photos:
            photos = photos[:max_photos]
        log.info("[%s] 待处理照片: %d", game, len(photos))

        total = len(photos)
        done = 0
        workers = max(1, self.cfg.workers)

        with ThreadPoolExecutor(max_workers=workers) as executor:
            futures = {
                executor.submit(self._process_one, game, p, force=force): p for p in photos
            }
            for future in as_completed(futures):
                photo = futures[future]
                done += 1
                try:
                    result = future.result()
                    if result == "synced":
                        stats.synced += 1
                    elif result == "skipped":
                        stats.skipped += 1
                except ForzaSyncError as exc:
                    stats.failed += 1
                    stats.failed_items.append((photo.photo_url, str(exc)))
                    log.error("[%s] 照片处理失败: %s", game, exc)
                except Exception as exc:  # 未知异常兜底
                    stats.failed += 1
                    stats.failed_items.append((photo.photo_url, str(exc)))
                    log.exception("[%s] 照片处理出现未预期错误", game)

                if done == total or done % 10 == 0:
                    log.info(
                        "[%s] 进度 %d/%d（新增 %d / 跳过 %d / 失败 %d）",
                        game,
                        done,
                        total,
                        stats.synced,
                        stats.skipped,
                        stats.failed,
                    )
                if progress_cb is not None:
                    progress_cb(game, done, total, stats)

        # 记录同步状态
        self.db.update_sync_state(
            game=game,
            total_records=total,
            synced_records=self.db.count_photos(game),
            last_sync_at=datetime.now(timezone.utc).isoformat(),
        )
        return stats

    # ------------------------------------------------------------------
    # 单张照片处理
    # ------------------------------------------------------------------
    def _process_one(self, game: str, photo: Photo, *, force: bool) -> str:
        """处理单张照片：判断增量 → 下载 → 写元数据 → 入库。"""
        photo_id = extract_photo_id(photo.photo_url)
        title = photo.title or ""

        # 增量判断：以 photo_id 为准（不依赖文件名）
        if not force and self.db.photo_exists(photo_id):
            return "skipped"

        rel_parts = build_relative_path(
            game=game,
            submission_time_utc=photo.submission_time_utc,
            title=title,
            photo_id=photo_id,
        )
        dest: Path = self.download_dir.joinpath(*rel_parts)

        # 文件已存在但未入库（例如上次中断）：补录数据库，避免重复下载
        if dest.exists() and not force:
            self.db.upsert_photo(
                photo_id=photo_id,
                game=game,
                title=title,
                description=photo.description,
                submission_time_utc=photo.submission_time_utc,
                url=photo.photo_url,
                local_path=str(dest),
            )
            return "skipped"

        # 下载原图
        self.downloader.download(photo.photo_url, dest)

        # 入库（照片详细信息直接存入数据库，不再生成 .json 元数据文件）
        self.db.upsert_photo(
            photo_id=photo_id,
            game=game,
            title=title,
            description=photo.description,
            submission_time_utc=photo.submission_time_utc,
            url=photo.photo_url,
            local_path=str(dest),
        )
        log.info("[%s] 已下载: %s", game, dest)
        return "synced"
