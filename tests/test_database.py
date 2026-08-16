"""数据库层测试。"""

from forza_sync.database import PhotoDatabase


def _make_db(tmp_path):
    db = PhotoDatabase(tmp_path / "test.db")
    db.connect()
    return db


class TestPhotos:
    def test_upsert_and_exists(self, tmp_path):
        db = _make_db(tmp_path)
        assert db.photo_exists("p1") is False
        db.upsert_photo(
            photo_id="p1",
            game="FH5",
            title="标题",
            description=None,
            submission_time_utc="2024-02-16T11:24:27Z",
            url="https://x/1.jpg",
            local_path="C:/ForzaPhotos/FH5/2024/02/1.jpg",
        )
        assert db.photo_exists("p1") is True
        assert db.count_photos() == 1
        assert db.count_photos("FH5") == 1
        assert db.count_photos("FH6") == 0

    def test_upsert_updates_instead_of_duplicate(self, tmp_path):
        db = _make_db(tmp_path)
        db.upsert_photo(
            photo_id="p1", game="FH5", title="a", description=None,
            submission_time_utc="t", url="https://x/1.jpg", local_path="x",
        )
        db.upsert_photo(
            photo_id="p1", game="FH5", title="b", description=None,
            submission_time_utc="t", url="https://x/1.jpg", local_path="y",
        )
        assert db.count_photos() == 1
        row = db.get_photo("p1")
        assert row["title"] == "b"
        assert row["local_path"] == "y"

    def test_count_by_game(self, tmp_path):
        db = _make_db(tmp_path)
        for i, g in enumerate(("FH5", "FH5", "FH6")):
            db.upsert_photo(
                photo_id=f"{g}_{i}", game=g, title="t", description=None,
                submission_time_utc="t", url=f"https://x/{i}.jpg", local_path="l",
            )
        counts = {row["game"]: row["n"] for row in db.count_by_game()}
        assert counts == {"FH5": 2, "FH6": 1}

    def test_all_photos(self, tmp_path):
        db = _make_db(tmp_path)
        db.upsert_photo(
            photo_id="p1", game="FH5", title="t", description=None,
            submission_time_utc="2024-01-01T00:00:00Z", url="u1", local_path="l1",
        )
        db.upsert_photo(
            photo_id="p2", game="FH5", title="t", description=None,
            submission_time_utc="2024-02-01T00:00:00Z", url="u2", local_path="l2",
        )
        rows = db.all_photos("FH5")
        assert [r["photo_id"] for r in rows] == ["p1", "p2"]


class TestSyncState:
    def test_update_and_get(self, tmp_path):
        db = _make_db(tmp_path)
        db.update_sync_state(game="FH5", total_records=100, synced_records=50)
        states = db.get_sync_state("FH5")
        assert len(states) == 1
        assert states[0]["total_records"] == 100
        assert states[0]["synced_records"] == 50
        assert states[0]["last_sync_at"]

    def test_update_overwrites(self, tmp_path):
        db = _make_db(tmp_path)
        db.update_sync_state(game="FH5", total_records=10, synced_records=5)
        db.update_sync_state(game="FH5", total_records=20, synced_records=10)
        states = db.get_sync_state("FH5")
        assert len(states) == 1
        assert states[0]["total_records"] == 20


def test_close_and_reopen(tmp_path):
    path = tmp_path / "test.db"
    db = PhotoDatabase(path).connect()
    db.upsert_photo(
        photo_id="p1", game="FH5", title="t", description=None,
        submission_time_utc="t", url="u", local_path="l",
    )
    db.close()

    db2 = PhotoDatabase(path).connect()
    assert db2.count_photos() == 1
    db2.close()
