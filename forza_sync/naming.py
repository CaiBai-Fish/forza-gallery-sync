"""文件名生成与净化逻辑。

文件名格式：{YYYYMMDD_HHMMSS}_{标题}_{photoId}.jpg
标题为空时： {YYYYMMDD_HHMMSS}_{photoId}.jpg

目录结构：download_dir/{GAME}/{YYYY}/{MM}/
"""

from __future__ import annotations

import hashlib
import re
from datetime import datetime, timezone
from pathlib import PurePosixPath
from typing import Optional, Tuple
from urllib.parse import unquote, urlparse

from .errors import DataFormatError

# Windows 不允许出现在文件名中的字符 + 控制字符
INVALID_FILENAME_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
WHITESPACE_RUNS = re.compile(r"\s+")

# 文件名（不含扩展名）最大长度，Windows 限制 255 字节，中文按 1 字符计更保守
MAX_FILENAME_LEN = 120

UUID_PATTERN = re.compile(
    r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
)


# ---------------------------------------------------------------------------
# 时间解析与格式化
# ---------------------------------------------------------------------------
def parse_utc(value: str) -> datetime:
    """将 ISO8601 时间字符串解析为 UTC datetime。"""
    if not isinstance(value, str) or not value.strip():
        raise DataFormatError(f"无法解析时间: {value!r}")
    s = value.strip().replace("Z", "+00:00")
    # 兼容 Python 3.9/3.10：fromisoformat 最多接受 6 位小数秒，截断多余位数
    s = re.sub(r"(\.\d{6})\d+", r"\1", s)
    dt: Optional[datetime] = None
    try:
        dt = datetime.fromisoformat(s)
    except ValueError:
        for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S"):
            try:
                dt = datetime.strptime(s, fmt)
                break
            except ValueError:
                continue
    if dt is None:
        raise DataFormatError(f"无法解析时间: {value!r}")
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def parse_utc_safe(value: str) -> datetime:
    """容错版本：解析失败时回退到当前 UTC 时间，避免单个坏数据中断同步。"""
    try:
        return parse_utc(value)
    except DataFormatError:
        return datetime.now(timezone.utc)


def format_timestamp(value: str) -> str:
    """返回 YYYYMMDD_HHMMSS 形式的时间戳（UTC）。"""
    return parse_utc_safe(value).strftime("%Y%m%d_%H%M%S")


def year_month_subdir(value: str) -> Tuple[str, str]:
    """返回 (年, 月) 两位格式，用于按日期分目录。"""
    dt = parse_utc_safe(value)
    return f"{dt.year:04d}", f"{dt.month:02d}"


# ---------------------------------------------------------------------------
# 文件名净化
# ---------------------------------------------------------------------------
def sanitize_filename_part(text: str, max_len: int = MAX_FILENAME_LEN) -> str:
    """净化标题片段：去掉非法字符、压缩空白、限制长度、去除首尾点/空格/下划线。"""
    if not text:
        return ""
    cleaned = INVALID_FILENAME_CHARS.sub("", str(text))
    cleaned = WHITESPACE_RUNS.sub("_", cleaned).strip(" ._")
    if len(cleaned) > max_len:
        cleaned = cleaned[:max_len].rstrip(" ._")
    return cleaned


# ---------------------------------------------------------------------------
# photo ID 提取
# ---------------------------------------------------------------------------
def extract_photo_id(url: str) -> str:
    """从 photoCdnPath 中提取照片唯一 ID。

    优先级：
    1. URL 中**最后一个** UUID（photoCdnPath 结构为
       .../galleryv2images/{图库ID}/{photo UUID}/{版本}，照片 UUID 在最后）
    2. URL 最后一段文件名（去掉扩展名）
    3. 上述均不可用时，取 URL 的 SHA-256 前 32 位（保证唯一且稳定）
    """
    if not url or not isinstance(url, str):
        raise DataFormatError("photoCdnPath 缺失或非法")

    # 取最后一个 UUID：前面的 UUID 可能是图库 ID
    uuids = UUID_PATTERN.findall(url)
    if uuids:
        return uuids[-1].lower()

    # 取路径最后一段并解码 URL 转义
    name = unquote(PurePosixPath(urlparse(url).path).name)
    stem = name.rsplit(".", 1)[0] if "." in name else name
    stem = sanitize_filename_part(stem, max_len=64)
    if stem:
        return stem

    return hashlib.sha256(url.encode("utf-8")).hexdigest()[:32]


# ---------------------------------------------------------------------------
# 文件名构建
# ---------------------------------------------------------------------------
def build_filename(
    *,
    submission_time_utc: str,
    title: str,
    photo_id: str,
    extension: str = ".jpg",
) -> str:
    """构建符合规范的本地文件名。"""
    ts = format_timestamp(submission_time_utc)
    pid = sanitize_filename_part(photo_id, max_len=48)
    if not pid:
        pid = hashlib.sha256(submission_time_utc.encode("utf-8")).hexdigest()[:16]

    clean_title = sanitize_filename_part(title)
    if clean_title:
        base = f"{ts}_{clean_title}_{pid}"
    else:
        base = f"{ts}_{pid}"
    return base + extension


def build_relative_path(
    *,
    game: str,
    submission_time_utc: str,
    title: str,
    photo_id: str,
    extension: str = ".jpg",
) -> Tuple[str, ...]:
    """构建相对路径片段，如 (FH5, 2024, 02, 20240216_112427_符华_442a6e68.jpg)。"""
    year, month = year_month_subdir(submission_time_utc)
    filename = build_filename(
        submission_time_utc=submission_time_utc,
        title=title,
        photo_id=photo_id,
        extension=extension,
    )
    return (game, year, month, filename)
