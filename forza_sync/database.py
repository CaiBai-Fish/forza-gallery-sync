"""SQLite 数据库层：记录已下载照片与同步状态，支撑增量同步。

表结构：
- photos：照片记录，photo_id 为主键（从 URL 提取的唯一 ID），url 唯一
- sync_state：每个游戏的最近同步状态
"""

from __future__ import annotations

import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional

SCHEMA = """
CREATE TABLE IF NOT EXISTS photos (
    photo_id            TEXT PRIMARY KEY,
    game                TEXT NOT NULL,
    title               TEXT,
    description         TEXT,
    submission_time_utc TEXT,
    url                 TEXT NOT NULL UNIQUE,
    local_path          TEXT,
    downloaded_at       TEXT,
    updated_at          TEXT
);

CREATE TABLE IF NOT EXISTS sync_state (
    game           TEXT PRIMARY KEY,
    last_sync_at   TEXT,
    total_records  INTEGER DEFAULT 0,
    synced_records INTEGER DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_photos_game ON photos (game);
"""


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


class PhotoDatabase:
    """线程安全的 SQLite 访问层。"""

    def __init__(self, db_path):
        self.db_path = Path(db_path)
        self._lock = threading.RLock()
        self._conn: Optional[sqlite3.Connection] = None

    # ------------------------------------------------------------------
    # 生命周期
    # ------------------------------------------------------------------
    def connect(self) -> "PhotoDatabase":
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(str(self.db_path), check_same_thread=False)
        self._conn.row_factory = sqlite3.Row
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA synchronous=NORMAL")
        self._conn.executescript(SCHEMA)
        self._conn.commit()
        return self

    def close(self) -> None:
        with self._lock:
            if self._conn is not None:
                self._conn.close()
                self._conn = None

    def __enter__(self) -> "PhotoDatabase":
        if self._conn is None:
            self.connect()
        return self

    def __exit__(self, *exc_info) -> None:
        self.close()

    # ------------------------------------------------------------------
    # photos 表
    # ------------------------------------------------------------------
    def photo_exists(self, photo_id: str) -> bool:
        with self._lock:
            cur = self._conn.execute(
                "SELECT 1 FROM photos WHERE photo_id = ?", (photo_id,)
            )
            return cur.fetchone() is not None

    def get_photo(self, photo_id: str) -> Optional[sqlite3.Row]:
        with self._lock:
            cur = self._conn.execute(
                "SELECT * FROM photos WHERE photo_id = ?", (photo_id,)
            )
            return cur.fetchone()

    def upsert_photo(
        self,
        *,
        photo_id: str,
        game: str,
        title: Optional[str],
        description: Optional[str],
        submission_time_utc: str,
        url: str,
        local_path: str,
    ) -> None:
        now = _now()
        with self._lock:
            self._conn.execute(
                """
                INSERT INTO photos
                    (photo_id, game, title, description, submission_time_utc,
                     url, local_path, downloaded_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(photo_id) DO UPDATE SET
                    game = excluded.game,
                    title = excluded.title,
                    description = excluded.description,
                    submission_time_utc = excluded.submission_time_utc,
                    url = excluded.url,
                    local_path = excluded.local_path,
                    updated_at = excluded.updated_at
                """,
                (
                    photo_id,
                    game,
                    title,
                    description,
                    submission_time_utc,
                    url,
                    local_path,
                    now,
                    now,
                ),
            )
            self._conn.commit()

    def count_photos(self, game: Optional[str] = None) -> int:
        with self._lock:
            if game:
                cur = self._conn.execute(
                    "SELECT COUNT(*) FROM photos WHERE game = ?", (game,)
                )
            else:
                cur = self._conn.execute("SELECT COUNT(*) FROM photos")
            return int(cur.fetchone()[0])

    def count_by_game(self) -> List[sqlite3.Row]:
        with self._lock:
            cur = self._conn.execute(
                "SELECT game, COUNT(*) AS n FROM photos GROUP BY game ORDER BY game"
            )
            return cur.fetchall()

    def all_photos(self, game: Optional[str] = None) -> List[sqlite3.Row]:
        with self._lock:
            if game:
                cur = self._conn.execute(
                    "SELECT * FROM photos WHERE game = ? ORDER BY submission_time_utc",
                    (game,),
                )
            else:
                cur = self._conn.execute(
                    "SELECT * FROM photos ORDER BY submission_time_utc"
                )
            return cur.fetchall()

    # ------------------------------------------------------------------
    # sync_state 表
    # ------------------------------------------------------------------
    def update_sync_state(
        self,
        *,
        game: str,
        total_records: int,
        synced_records: int,
        last_sync_at: Optional[str] = None,
    ) -> None:
        now = last_sync_at or _now()
        with self._lock:
            self._conn.execute(
                """
                INSERT INTO sync_state (game, last_sync_at, total_records, synced_records)
                VALUES (?, ?, ?, ?)
                ON CONFLICT(game) DO UPDATE SET
                    last_sync_at = excluded.last_sync_at,
                    total_records = excluded.total_records,
                    synced_records = excluded.synced_records
                """,
                (game, now, total_records, synced_records),
            )
            self._conn.commit()

    def get_sync_state(self, game: Optional[str] = None) -> List[sqlite3.Row]:
        with self._lock:
            if game:
                cur = self._conn.execute(
                    "SELECT * FROM sync_state WHERE game = ?", (game,)
                )
            else:
                cur = self._conn.execute(
                    "SELECT * FROM sync_state ORDER BY game"
                )
            return cur.fetchall()
