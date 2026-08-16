"""命名与文件名逻辑测试。"""

import pytest

from forza_sync.errors import DataFormatError
from forza_sync.naming import (
    build_filename,
    build_relative_path,
    extract_photo_id,
    format_timestamp,
    sanitize_filename_part,
    year_month_subdir,
)


class TestSanitize:
    def test_removes_windows_invalid_chars(self):
        assert sanitize_filename_part('a<b>c:d"e/f\\g|h?i*j') == "abcdefghij"

    def test_removes_control_chars(self):
        assert sanitize_filename_part("ab\x00\x1fcd") == "abcd"

    def test_collapses_whitespace_to_underscore(self):
        assert sanitize_filename_part("  my   photo  ") == "my_photo"

    def test_strips_trailing_dot_and_space(self):
        assert sanitize_filename_part("photo. ") == "photo"

    def test_chinese_kept(self):
        assert sanitize_filename_part("符华") == "符华"

    def test_truncates_long_text(self):
        long = "x" * 300
        assert len(sanitize_filename_part(long)) <= 120

    def test_empty_returns_empty(self):
        assert sanitize_filename_part("") == ""
        assert sanitize_filename_part(None) == ""


class TestExtractPhotoId:
    def test_uuid_in_url(self):
        url = "https://cdn.example.com/fh5/442a6e68-1a2b-3c4d-5e6f-708192a3b4c5/orig.jpg"
        assert extract_photo_id(url) == "442a6e68-1a2b-3c4d-5e6f-708192a3b4c5"

    def test_last_uuid_when_gallery_id_and_photo_id(self):
        # photoCdnPath 真实结构: .../galleryv2images/{图库ID}/{photo UUID}/{版本}
        url = ("https://t10pgalleryv2.azureedge.net/galleryv2images/"
               "d57985b9-3af4-41f0-ba6e-56e0582416fc/"
               "e1d237ac-cbac-492a-8f2a-de37e61cc687/2")
        assert extract_photo_id(url) == "e1d237ac-cbac-492a-8f2a-de37e61cc687"

    def test_uuid_normalized_to_lower(self):
        url = "https://x/ABC12345-ABCD-ABCD-ABCD-ABCDEF123456/x.jpg"
        assert extract_photo_id(url) == "abc12345-abcd-abcd-abcd-abcdef123456"

    def test_filename_stem(self):
        url = "https://x/path/to/442a6e68.jpg"
        assert extract_photo_id(url) == "442a6e68"

    def test_hashes_when_no_name(self):
        # 路径无文件名 → 回退到 sha256 哈希
        url = "https://example.com/?id=abc"
        pid = extract_photo_id(url)
        assert len(pid) == 32  # sha256 前 32 位
        assert pid == extract_photo_id(url)  # 稳定

    def test_missing_url_raises(self):
        with pytest.raises(DataFormatError):
            extract_photo_id("")
        with pytest.raises(DataFormatError):
            extract_photo_id(None)


class TestBuildFilename:
    def test_with_title(self):
        name = build_filename(
            submission_time_utc="2024-02-16T11:24:27Z",
            title="符华",
            photo_id="442a6e68",
        )
        assert name == "20240216_112427_符华_442a6e68.jpg"

    def test_empty_title(self):
        name = build_filename(
            submission_time_utc="2024-02-16T11:24:27Z",
            title="",
            photo_id="442a6e68",
        )
        assert name == "20240216_112427_442a6e68.jpg"

    def test_title_with_spaces_and_invalid_chars(self):
        name = build_filename(
            submission_time_utc="2024-02-16T11:24:27Z",
            title="my : photo / 1",
            photo_id="442a6e68",
        )
        assert name == "20240216_112427_my_photo_1_442a6e68.jpg"

    def test_timezone_utc_conversion(self):
        # UTC+8 的下午应转为 UTC 早晨
        name = build_filename(
            submission_time_utc="2024-02-16T19:24:27+08:00",
            title="t",
            photo_id="p",
        )
        assert name == "20240216_112427_t_p.jpg"


class TestRelativePath:
    def test_path_structure(self):
        rel = build_relative_path(
            game="FH5",
            submission_time_utc="2024-02-16T11:24:27Z",
            title="符华",
            photo_id="442a6e68",
        )
        assert rel[0] == "FH5"
        assert rel[1] == "2024"
        assert rel[2] == "02"
        assert rel[3] == "20240216_112427_符华_442a6e68.jpg"


class TestTimeHelpers:
    def test_format_timestamp(self):
        assert format_timestamp("2024-02-16T11:24:27Z") == "20240216_112427"

    def test_format_timestamp_with_fraction(self):
        assert format_timestamp("2024-02-16T11:24:27.123Z") == "20240216_112427"

    def test_format_timestamp_with_seven_fraction_digits(self):
        # API 真实返回 7 位小数秒
        assert format_timestamp("2025-11-19T10:32:58.3864929+00:00") == "20251119_103258"

    def test_year_month(self):
        assert year_month_subdir("2026-08-03T09:00:00Z") == ("2026", "08")

    def test_bad_time_falls_back(self):
        # 不抛异常，回退当前时间
        ts = format_timestamp("not-a-time")
        assert len(ts) == 15  # YYYYMMDD_HHMMSS
