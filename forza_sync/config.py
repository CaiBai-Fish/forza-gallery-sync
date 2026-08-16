"""配置管理：配置与代码分离，默认存储在用户配置目录。

配置默认路径（可通过 FORZA_SYNC_CONFIG 环境变量覆盖）：
- Windows: %APPDATA%\\forza-sync\\config.json
- Linux/macOS: $XDG_CONFIG_HOME/forza-sync/config.json 或 ~/.config/forza-sync/config.json
"""

from __future__ import annotations

import json
import os
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Optional

from .errors import ConfigError

# 当前支持的 Forza 游戏
SUPPORTED_GAMES = ("FH5", "FH6")

DEFAULT_PAGE_SIZE = 50
# 分页方案：auto（自动探测）/ page / skip / offset / page_number / none
DEFAULT_PAGINATION = "auto"

CONFIG_ENV_VAR = "FORZA_SYNC_CONFIG"


def _default_config_dir() -> Path:
    """返回平台相关的配置目录。"""
    if sys.platform == "win32":
        base = os.environ.get("APPDATA") or str(Path.home())
        return Path(base) / "forza-sync"
    base = os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "forza-sync"


def default_config_path() -> Path:
    """返回配置文件路径，支持环境变量覆盖。"""
    env = os.environ.get(CONFIG_ENV_VAR)
    if env:
        return Path(env).expanduser()
    return _default_config_dir() / "config.json"


@dataclass
class Config:
    """工具配置。所有字段均有安全默认值。"""

    token: str = ""
    refresh_token: str = ""  # 用于自动刷新 access token
    token_issued_at: str = ""  # 最近一次获取/刷新 access token 的时间（ISO8601 UTC）
    token_expires_in: int = 0  # access token 有效期（秒）
    download_dir: str = ""  # 为空时默认 ~/ForzaPhotos
    database_path: str = ""  # 为空时默认 <配置目录>/forza_sync.db
    page_size: int = DEFAULT_PAGE_SIZE
    pagination: str = DEFAULT_PAGINATION
    timeout: int = 30
    retries: int = 3
    workers: int = 4
    verify_ssl: bool = True
    user_agent: str = "forza-sync/0.1.0"
    enabled_games: list = field(default_factory=lambda: list(SUPPORTED_GAMES))

    # ---- 反序列化 ----
    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "Config":
        cfg = cls()
        if not isinstance(data, dict):
            raise ConfigError("配置文件内容不是有效的 JSON 对象")

        cfg.token = _as_str(data.get("token", ""))
        cfg.refresh_token = _as_str(data.get("refresh_token", ""))
        cfg.token_issued_at = _as_str(data.get("token_issued_at", ""))
        cfg.token_expires_in = _as_int(data.get("token_expires_in", 0), 0)
        cfg.download_dir = _as_str(data.get("download_dir", ""))
        cfg.database_path = _as_str(data.get("database_path", ""))
        cfg.page_size = _as_int(data.get("page_size", DEFAULT_PAGE_SIZE), DEFAULT_PAGE_SIZE)
        cfg.pagination = _as_str(data.get("pagination", DEFAULT_PAGINATION))
        if cfg.pagination not in ("auto", "page", "skip", "offset", "page_number", "none"):
            cfg.pagination = DEFAULT_PAGINATION
        cfg.timeout = _as_int(data.get("timeout", 30), 30)
        cfg.retries = _as_int(data.get("retries", 3), 3)
        cfg.workers = _as_int(data.get("workers", 4), 4)
        cfg.verify_ssl = bool(data.get("verify_ssl", True))
        cfg.user_agent = _as_str(data.get("user_agent", "forza-sync/0.1.0"))

        games = data.get("enabled_games")
        if isinstance(games, list) and games:
            cfg.enabled_games = [g for g in games if isinstance(g, str) and g in SUPPORTED_GAMES]
            if not cfg.enabled_games:
                cfg.enabled_games = list(SUPPORTED_GAMES)

        cfg.validate()
        return cfg

    # ---- 序列化 ----
    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    # ---- 校验 ----
    def validate(self) -> None:
        if self.page_size < 1:
            raise ConfigError("page_size 必须大于 0")
        if self.timeout < 1:
            raise ConfigError("timeout 必须大于 0")
        if self.retries < 0:
            raise ConfigError("retries 不能为负数")
        if self.workers < 1:
            raise ConfigError("workers 必须大于 0")
        if self.token_expires_in < 0:
            raise ConfigError("token_expires_in 不能为负数")

    # ---- 路径解析 ----
    def effective_download_dir(self) -> Path:
        if self.download_dir:
            return Path(self.download_dir).expanduser()
        return Path.home() / "ForzaPhotos"

    def effective_database_path(self, config_dir: Path) -> Path:
        if self.database_path:
            return Path(self.database_path).expanduser()
        return config_dir / "forza_sync.db"


def _as_str(value: Any, default: str = "") -> str:
    return value if isinstance(value, str) else default


def _as_int(value: Any, default: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


class ConfigManager:
    """负责配置的加载与保存。"""

    def __init__(self, config_path: Optional[Path] = None):
        self.path = Path(config_path) if config_path else default_config_path()

    def load(self) -> Config:
        if not self.path.exists():
            return Config()
        try:
            raw = self.path.read_text(encoding="utf-8")
            data = json.loads(raw)
        except (OSError, json.JSONDecodeError) as exc:
            raise ConfigError(f"无法读取配置文件 {self.path}: {exc}") from exc
        return Config.from_dict(data)

    def save(self, cfg: Config) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp_path = self.path.with_name(self.path.name + ".tmp")
        try:
            tmp_path.write_text(
                json.dumps(cfg.to_dict(), ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            os.replace(tmp_path, self.path)
        except OSError as exc:
            raise ConfigError(f"无法写入配置文件 {self.path}: {exc}") from exc

    def exists(self) -> bool:
        return self.path.exists()
