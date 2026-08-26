"""图片下载。

- 原子写入：先下载到临时文件，完成后重命名，避免半截文件
- 失败重试：网络异常 / 空内容按指数退避重试
- 元数据由同步服务直接写入 SQLite 数据库（不再生成 .json 文件）
"""

from __future__ import annotations

import logging
import os
import time
from pathlib import Path
from typing import Optional

import requests

from .errors import DownloadError, NetworkError

log = logging.getLogger(__name__)

CHUNK_SIZE = 64 * 1024


class ImageDownloader:
    """图片下载器。"""

    def __init__(
        self,
        *,
        timeout: int = 30,
        retries: int = 3,
        verify_ssl: bool = True,
        user_agent: str = "forza-sync",
    ):
        self.timeout = timeout
        self.retries = max(1, retries)
        self.verify_ssl = verify_ssl
        self.session = requests.Session()
        self.session.headers.update({"User-Agent": user_agent})

    def download(self, url: str, dest_path: Path) -> Path:
        """下载 url 到 dest_path，成功返回最终路径。

        失败时抛出 :class:`DownloadError` 或 :class:`NetworkError`。
        """
        dest_path.parent.mkdir(parents=True, exist_ok=True)
        tmp_path = dest_path.with_name(dest_path.name + ".part")

        last_exc: Optional[Exception] = None
        for attempt in range(1, self.retries + 1):
            try:
                with self.session.get(
                    url, stream=True, timeout=self.timeout, verify=self.verify_ssl
                ) as resp:
                    if resp.status_code in (401, 403):
                        raise DownloadError(f"下载被拒绝（{resp.status_code}）: {url}")
                    resp.raise_for_status()

                    total = 0
                    with open(tmp_path, "wb") as fh:
                        for chunk in resp.iter_content(chunk_size=CHUNK_SIZE):
                            if chunk:
                                fh.write(chunk)
                                total += len(chunk)
                    if total == 0:
                        raise DownloadError(f"下载内容为空: {url}")

                    os.replace(tmp_path, dest_path)
                    return dest_path
            except (requests.exceptions.ConnectionError, requests.exceptions.Timeout) as exc:
                last_exc = NetworkError(f"下载网络异常: {exc}")
                log.warning("下载网络异常（第 %d/%d 次）: %s", attempt, self.retries, exc)
            except requests.exceptions.HTTPError as exc:
                raise DownloadError(f"下载失败: {exc}") from exc
            except DownloadError:
                raise

            if attempt < self.retries:
                time.sleep(min(2**attempt, 10))

        raise last_exc if last_exc else DownloadError(f"下载失败: {url}")
