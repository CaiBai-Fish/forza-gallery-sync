"""同步服务单元测试（使用临时目录与假下载器，不访问网络）。"""

from pathlib import Path

from forza_sync.api_client import Photo
from forza_sync.config import Config
from forza_sync.database import PhotoDatabase
from forza_sync.sync import SyncService


class FakeDownloader:
    """将 URL 写入目标文件的假下载器。"""

    def download(self, url, dest_path):
        dest_path.parent.mkdir(parents=True, exist_ok=True)
        dest_path.write_bytes(b"fake-image-bytes:" + url.encode())


def _make_service(tmp_path, monkeypatch, download_dir=None):
    cfg = Config(
        token="t",
        download_dir=str(download_dir or tmp_path / "photos"),
        page_size=50,
        pagination="none",
    )
    db = PhotoDatabase(tmp_path / "sync.db").connect()
    service = SyncService(cfg, db)
    service.downloader = FakeDownloader()
    return service, db


def _photo(game="FH5", title="符华", url="https://x/442a6e68.jpg", time="2024-02-16T11:24:27Z"):
    return Photo(
        game=game,
        title=title,
        description="desc",
        submission_time_utc=time,
        photo_url=url,
        thumbnail_url=None,
        preview_url=None,
    )


class TestProcessOne:
    def test_downloads_and_records(self, tmp_path, monkeypatch):
        service, db = _make_service(tmp_path, monkeypatch)
        result = service._process_one("FH5", _photo(), force=False)
        assert result == "synced"
        assert db.count_photos("FH5") == 1

        # 文件名符合规范
        row = db.get_photo("442a6e68")
        rel = Path(row["local_path"]).relative_to(service.download_dir)
        assert rel.parts == ("FH5", "2024", "02", "20240216_112427_符华_442a6e68.jpg")

        # 图片与元数据均已落盘
        assert Path(row["local_path"]).exists()
        meta = Path(row["local_path"]).with_suffix(".json")
        assert meta.exists()

    def test_skips_existing_in_db(self, tmp_path, monkeypatch):
        service, db = _make_service(tmp_path, monkeypatch)
        assert service._process_one("FH5", _photo(), force=False) == "synced"
        # 再次处理同一张 → 跳过（不重复下载）
        assert service._process_one("FH5", _photo(), force=False) == "skipped"
        assert db.count_photos() == 1

    def test_force_redownloads(self, tmp_path, monkeypatch):
        service, db = _make_service(tmp_path, monkeypatch)
        assert service._process_one("FH5", _photo(), force=False) == "synced"
        assert service._process_one("FH5", _photo(), force=True) == "synced"
        assert db.count_photos() == 1

    def test_file_exists_but_not_in_db_records_without_download(self, tmp_path, monkeypatch):
        service, db = _make_service(tmp_path, monkeypatch)
        photo = _photo()
        # 预先放置文件但数据库无记录
        dest = service.download_dir / "FH5" / "2024" / "02" / "20240216_112427_符华_442a6e68.jpg"
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(b"existing")
        result = service._process_one("FH5", photo, force=False)
        assert result == "skipped"
        assert db.count_photos() == 1

    def test_empty_title_path(self, tmp_path, monkeypatch):
        service, db = _make_service(tmp_path, monkeypatch)
        photo = _photo(title="", url="https://x/442a6e68.jpg")
        service._process_one("FH5", photo, force=False)
        row = db.get_photo("442a6e68")
        rel = Path(row["local_path"]).relative_to(service.download_dir)
        assert rel.parts[-1] == "20240216_112427_442a6e68.jpg"


class TestSyncGameStats:
    def test_stats_counts(self, tmp_path, monkeypatch):
        service, db = _make_service(tmp_path, monkeypatch)

        class FakeClient:
            def fetch_all(self, game, scheme, page_size):
                return [
                    _photo(title="a", url="https://x/1.jpg"),
                    _photo(title="b", url="https://x/2.jpg"),
                ]

        service.client = FakeClient()
        stats = service.sync_game("FH5")
        assert stats.synced == 2
        assert stats.skipped == 0
        assert stats.failed == 0

        # 再次同步全部跳过
        stats2 = service.sync_game("FH5")
        assert stats2.synced == 0
        assert stats2.skipped == 2

        # 同步状态已记录
        states = db.get_sync_state("FH5")
        assert states[0]["total_records"] == 2
        assert states[0]["synced_records"] == 2

    def test_sync_failure_collected(self, tmp_path, monkeypatch):
        service, db = _make_service(tmp_path, monkeypatch)

        class FailingDownloader:
            def download(self, url, dest_path):
                raise Exception("boom")

        service.downloader = FailingDownloader()

        class FakeClient:
            def fetch_all(self, game, scheme, page_size):
                return [_photo(url="https://x/1.jpg")]

        service.client = FakeClient()
        stats = service.sync_game("FH5")
        assert stats.failed == 1
        assert stats.synced == 0
        assert len(stats.failed_items) == 1
