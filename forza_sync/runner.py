"""后台同步运行器：在应用进程内执行同步，前端轮询进度。

使用独立的线程运行 :class:`forza_sync.sync.SyncService`，并通过
线程安全的全局状态对象向前端（Tauri / service 层）暴露实时进度与取消能力。
"""

from __future__ import annotations

import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional

from .auth import TokenManager
from .config import Config, ConfigManager
from .database import PhotoDatabase
from .sync import SyncService, SyncStats


class SyncProgress:
    """线程安全的同步进度状态。"""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.running = False
        self.cancel_requested = False
        self.game: str = ""
        self.games: List[str] = []
        self.total = 0
        self.done = 0
        self.synced = 0
        self.skipped = 0
        self.failed = 0
        self.failed_items: List[dict] = []
        self.message: str = ""
        self.started_at: Optional[str] = None
        self.finished_at: Optional[str] = None
        self.force = False
        self.max_photos: Optional[int] = None

    def snapshot(self) -> dict:
        with self._lock:
            return {
                "running": self.running,
                "cancel_requested": self.cancel_requested,
                "game": self.game,
                "games": list(self.games),
                "total": self.total,
                "done": self.done,
                "synced": self.synced,
                "skipped": self.skipped,
                "failed": self.failed,
                "failed_items": list(self.failed_items),
                "message": self.message,
                "started_at": self.started_at,
                "finished_at": self.finished_at,
                "force": self.force,
                "max_photos": self.max_photos,
            }

    def _set(self, **kwargs) -> None:
        with self._lock:
            for k, v in kwargs.items():
                setattr(self, k, v)


class SyncRunner:
    """管理一次同步任务的启动、进度与取消。"""

    def __init__(self) -> None:
        self.progress = SyncProgress()
        self._thread: Optional[threading.Thread] = None
        self._cancel = threading.Event()

    # ------------------------------------------------------------------
    def is_running(self) -> bool:
        return self.progress.running

    def start(
        self,
        *,
        config_path: Optional[Path],
        games: List[str],
        force: bool = False,
        max_photos: Optional[int] = None,
        page_size: Optional[int] = None,
    ) -> None:
        if self.progress.running:
            raise RuntimeError("已有同步任务在运行")
        self._cancel.clear()
        self.progress._set(
            running=True,
            cancel_requested=False,
            games=list(games),
            total=0,
            done=0,
            synced=0,
            skipped=0,
            failed=0,
            failed_items=[],
            message="正在准备…",
            started_at=datetime.now(timezone.utc).isoformat(),
            finished_at=None,
            force=force,
            max_photos=max_photos,
        )
        self._thread = threading.Thread(
            target=self._run,
            args=(config_path, list(games), force, max_photos, page_size),
            daemon=True,
        )
        self._thread.start()

    def stop(self) -> bool:
        """请求取消当前任务。返回是否成功发出取消信号。"""
        if not self.progress.running:
            return False
        self._cancel.set()
        self.progress._set(cancel_requested=True, message="正在取消…")
        return True

    # ------------------------------------------------------------------
    def _run(
        self,
        config_path: Optional[Path],
        games: List[str],
        force: bool,
        max_photos: Optional[int],
        page_size: Optional[int],
    ) -> None:
        try:
            mgr = ConfigManager(config_path)
            cfg = mgr.load()
            if not cfg.token:
                raise RuntimeError("未配置 Token，请先在设置中登录或填写 Token")

            if page_size and page_size > 0:
                cfg.page_size = max(1, int(page_size))

            db_path = cfg.effective_database_path(mgr.path.parent)
            token_manager = TokenManager(
                mgr.path,
                timeout=cfg.timeout,
                retries=cfg.retries,
                verify_ssl=cfg.verify_ssl,
                user_agent=cfg.user_agent,
            )

            grand = {"synced": 0, "skipped": 0, "failed": 0}
            with PhotoDatabase(db_path).connect() as db:
                service = SyncService(cfg, db, token_manager=token_manager)
                for game in games:
                    if self._cancel.is_set():
                        self.progress._set(message=f"已取消（{game} 之前）", running=False)
                        return
                    self.progress._set(game=game, message=f"正在同步 {game}…")

                    def _cb(game, done, total, stats: SyncStats):
                        self.progress._set(
                            game=game,
                            done=done,
                            total=total,
                            synced=stats.synced,
                            skipped=stats.skipped,
                            failed=stats.failed,
                            message=(
                                f"{game}：{done}/{total} "
                                f"（新增 {stats.synced} / 跳过 {stats.skipped} / 失败 {stats.failed}）"
                            ),
                        )
                        if self._cancel.is_set():
                            raise _CancelSync()

                    try:
                        stats = service.sync_game(
                            game,
                            force=force,
                            max_photos=max_photos,
                            progress_cb=_cb,
                        )
                    except _CancelSync:
                        self.progress._set(message=f"已取消（{game}）", running=False)
                        return

                    grand["synced"] += stats.synced
                    grand["skipped"] += stats.skipped
                    grand["failed"] += stats.failed
                    self.progress._set(
                        synced=grand["synced"],
                        skipped=grand["skipped"],
                        failed=grand["failed"],
                    )
                    if stats.failed_items:
                        self.progress._set(
                            failed_items=[
                                {"url": url, "reason": reason}
                                for url, reason in stats.failed_items[:50]
                            ]
                        )

            self.progress._set(
                message=(
                    f"同步完成：新增 {grand['synced']} / 跳过 {grand['skipped']} / "
                    f"失败 {grand['failed']}"
                ),
                running=False,
                finished_at=datetime.now(timezone.utc).isoformat(),
            )
        except _CancelSync:
            self.progress._set(message="已取消", running=False)
        except Exception as exc:  # 兜底：不因后台线程异常导致应用崩溃
            self.progress._set(
                message=f"同步失败：{exc}",
                running=False,
                finished_at=datetime.now(timezone.utc).isoformat(),
            )


class _CancelSync(Exception):
    """内部信号：从进度回调中抛出以中断同步。"""


# 全局单例
runner = SyncRunner()
