"""API 客户端解析与分页逻辑测试（不发起真实网络请求）。"""

import pytest

from forza_sync.api_client import ForzaGalleryClient, Photo
from forza_sync.errors import DataFormatError

SAMPLE_ITEM = {
    "title": "符华",
    "description": "描述",
    "submissionTimeUtc": "2024-02-16T11:24:27Z",
    "photoCdnPath": "https://cdn.example.com/fh5/442a6e68-1a2b-3c4d-5e6f-708192a3b4c5/o.jpg",
    "thumbnailCdnPath": "https://cdn.example.com/thumb.jpg",
    "previewCdnPath": "https://cdn.example.com/preview.jpg",
}


def _client():
    return ForzaGalleryClient("test-token")


class TestPhotoFromApi:
    def test_parse_ok(self):
        p = Photo.from_api(SAMPLE_ITEM, "FH5")
        assert p.game == "FH5"
        assert p.title == "符华"
        assert p.photo_url == SAMPLE_ITEM["photoCdnPath"]
        assert p.thumbnail_url == SAMPLE_ITEM["thumbnailCdnPath"]

    def test_missing_photo_cdn_raises(self):
        item = dict(SAMPLE_ITEM)
        item.pop("photoCdnPath")
        with pytest.raises(DataFormatError):
            Photo.from_api(item, "FH5")

    def test_photo_cdn_not_string_raises(self):
        item = dict(SAMPLE_ITEM, photoCdnPath=123)
        with pytest.raises(DataFormatError):
            Photo.from_api(item, "FH5")

    def test_not_a_dict_raises(self):
        with pytest.raises(DataFormatError):
            Photo.from_api(["x"], "FH5")

    def test_optional_fields_none(self):
        p = Photo.from_api({"photoCdnPath": "https://x/1.jpg"}, "FH6")
        assert p.title == ""
        assert p.description is None
        assert p.submission_time_utc == ""


class TestParseGallery:
    def test_parse_with_paging(self):
        data = {
            "results": [SAMPLE_ITEM],
            "pagingInfo": {"totalRecords": 3},
        }
        page = _client()._parse_gallery(data, "FH5")
        assert page.total_records == 3
        assert len(page.results) == 1
        assert page.results[0].title == "符华"

    def test_not_a_dict_raises(self):
        with pytest.raises(DataFormatError):
            _client()._parse_gallery([], "FH5")

    def test_missing_results_raises(self):
        with pytest.raises(DataFormatError):
            _client()._parse_gallery({"pagingInfo": {}}, "FH5")

    def test_total_fallback_to_results_len(self):
        data = {"results": [SAMPLE_ITEM]}
        page = _client()._parse_gallery(data, "FH5")
        assert page.total_records == 1

    def test_bad_record_skipped(self):
        data = {"results": [SAMPLE_ITEM, {"title": "坏数据"}], "pagingInfo": {"totalRecords": 2}}
        page = _client()._parse_gallery(data, "FH5")
        assert len(page.results) == 1


class TestBuildParams:
    def test_page_scheme(self):
        assert ForzaGalleryClient._build_params("page", page_num=2, page_size=50) == {
            "page": 2,
            "pageSize": 50,
        }

    def test_skip_scheme(self):
        assert ForzaGalleryClient._build_params("skip", page_num=100, page_size=50) == {
            "skip": 100,
            "take": 50,
        }

    def test_offset_scheme(self):
        assert ForzaGalleryClient._build_params("offset", page_num=100, page_size=50) == {
            "offset": 100,
            "limit": 50,
        }

    def test_page_number_scheme(self):
        assert ForzaGalleryClient._build_params("page_number", page_num=3, page_size=25) == {
            "pageNumber": 3,
            "pageSize": 25,
        }


class TestResolveScheme:
    def test_explicit_scheme_returned(self):
        assert _client().resolve_scheme("FH5", "skip", 50) == "skip"

    def test_auto_cached(self, monkeypatch):
        client = _client()
        calls = {"n": 0}

        def fake_probe(game, page_size):
            calls["n"] += 1
            return "page"

        monkeypatch.setattr(client, "_probe", fake_probe)
        assert client.resolve_scheme("FH5", "auto", 50) == "page"
        assert client.resolve_scheme("FH5", "auto", 50) == "page"
        assert calls["n"] == 1
