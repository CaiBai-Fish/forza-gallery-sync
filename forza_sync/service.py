"""纯函数服务层：供 PyO3 嵌入式桌面端调用。

不依赖任何 HTTP 框架，所有函数返回可 JSON 序列化的 dict / list / bytes，
由 Tauri（PyO3）原生命令直接调用。
"""

from __future__ import annotations

import base64
import json as _json
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

from .auth import TokenManager
from .config import SUPPORTED_GAMES, Config, ConfigManager, game_display_name
from .database import PhotoDatabase
from .errors import AuthError, ForzaSyncError
from .login import BrowserLogin, detect_system_browser
from .runner import runner  # 复用后台同步运行器单例

# ---------------------------------------------------------------------------
# 通用辅助
# ---------------------------------------------------------------------------


def _mask(token: str) -> str:
    if not token:
        return "(未设置)"
    if len(token) <= 8:
        return "*" * len(token)
    return f"{token[:4]}{'*' * (len(token) - 8)}{token[-4:]}"


def _get_mgr(config_path: Optional[str] = None) -> ConfigManager:
    return ConfigManager(Path(config_path) if config_path else None)


def _cfg_dict(cfg: Config, mgr: ConfigManager) -> dict:
    db_path = cfg.effective_database_path(mgr.path.parent)
    return {
        "token": cfg.token,
        "masked_token": _mask(cfg.token),
        "has_token": bool(cfg.token),
        "has_refresh_token": bool(cfg.refresh_token),
        "masked_refresh_token": _mask(cfg.refresh_token),
        "download_dir": cfg.effective_download_dir(),
        "database_path": str(db_path),
        "page_size": cfg.page_size,
        "pagination": cfg.pagination,
        "timeout": cfg.timeout,
        "retries": cfg.retries,
        "workers": cfg.workers,
        "verify_ssl": cfg.verify_ssl,
        "user_agent": cfg.user_agent,
        "enabled_games": list(cfg.enabled_games),
        "config_path": str(mgr.path),
        "supported_games": [
            {"id": code, "name": game_display_name(code)} for code in SUPPORTED_GAMES
        ],
    }


# ---------------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------------

# 允许修改的配置字段（白名单）
EDITABLE_CONFIG_KEYS = {
    "download_dir": "str",
    "page_size": "int",
    "pagination": "str",
    "timeout": "int",
    "retries": "int",
    "workers": "int",
    "verify_ssl": "bool",
    "user_agent": "str",
    "enabled_games": "games",
    "token": "str",
    "refresh_token": "str",
}


def get_config(config_path: Optional[str] = None) -> dict:
    mgr = _get_mgr(config_path)
    return _cfg_dict(mgr.load(), mgr)


def update_config(values: Dict[str, Any], config_path: Optional[str] = None) -> dict:
    mgr = _get_mgr(config_path)
    cfg = mgr.load()
    for key, value in values.items():
        kind = EDITABLE_CONFIG_KEYS.get(key)
        if kind is None:
            raise ValueError(f"不允许修改配置项: {key}")
        try:
            if kind == "int":
                setattr(cfg, key, int(value))
            elif kind == "bool":
                setattr(cfg, key, bool(value))
            elif kind == "games":
                games = [g.strip().upper() for g in value if isinstance(g, str)]
                cfg.enabled_games = [g for g in games if g in SUPPORTED_GAMES]
                if not cfg.enabled_games:
                    raise ValueError("没有合法的游戏名")
            else:
                setattr(cfg, key, str(value))
        except (TypeError, ValueError) as exc:
            raise ValueError(f"配置项 {key} 的值无效") from exc
    try:
        cfg.validate()
    except ForzaSyncError as exc:
        raise ValueError(str(exc)) from exc
    mgr.save(cfg)
    return _cfg_dict(cfg, mgr)


# ---------------------------------------------------------------------------
# 综合状态（仪表盘）
# ---------------------------------------------------------------------------


def _count_by_month(rows) -> List[dict]:
    """按 游戏/年-月 聚合照片数量，返回降序列表。"""
    buckets: Dict[str, int] = {}
    for r in rows:
        key = f"{r['game']}|{r['submission_time_utc'][:7]}"
        buckets[key] = buckets.get(key, 0) + 1
    items = [
        {"game": k.split("|")[0], "month": k.split("|")[1], "count": v}
        for k, v in buckets.items()
    ]
    items.sort(key=lambda x: (x["game"], x["month"]), reverse=True)
    return items


def get_status(config_path: Optional[str] = None) -> dict:
    mgr = _get_mgr(config_path)
    cfg = mgr.load()
    db_path = cfg.effective_database_path(mgr.path.parent)

    token_status = TokenManager(
        mgr.path,
        timeout=cfg.timeout,
        retries=cfg.retries,
        verify_ssl=cfg.verify_ssl,
        user_agent=cfg.user_agent,
    ).status()

    photos = {"total": 0, "by_game": [], "by_month": []}
    sync_state: List[dict] = []
    if db_path.exists():
        with PhotoDatabase(db_path).connect() as db:
            photos["total"] = db.count_photos()
            photos["by_game"] = [
                {"game": r["game"], "count": r["n"]} for r in db.count_by_game()
            ]
            photos["by_month"] = _count_by_month(db.all_photos())
            sync_state = [
                {
                    "game": r["game"],
                    "last_sync_at": r["last_sync_at"],
                    "total_records": r["total_records"],
                    "synced_records": r["synced_records"],
                }
                for r in db.get_sync_state()
            ]

    return {
        "config": _cfg_dict(cfg, mgr),
        "token": token_status,
        "photos": photos,
        "sync_state": sync_state,
        "sync": runner.progress.snapshot(),
    }


# ---------------------------------------------------------------------------
# 认证 / Token
# ---------------------------------------------------------------------------


def auth_status(config_path: Optional[str] = None) -> dict:
    mgr = _get_mgr(config_path)
    cfg = mgr.load()
    info = TokenManager(
        mgr.path,
        timeout=cfg.timeout,
        retries=cfg.retries,
        verify_ssl=cfg.verify_ssl,
        user_agent=cfg.user_agent,
    ).status()
    info["masked_token"] = _mask(cfg.token)
    info["masked_refresh_token"] = _mask(cfg.refresh_token)
    return info


def auth_refresh(config_path: Optional[str] = None) -> dict:
    mgr = _get_mgr(config_path)
    cfg = mgr.load()
    TokenManager(
        mgr.path,
        timeout=cfg.timeout,
        retries=cfg.retries,
        verify_ssl=cfg.verify_ssl,
        user_agent=cfg.user_agent,
    ).refresh()
    return {"ok": True, "message": "Token 已刷新"}


# ---- 浏览器登录（后台线程 + 状态轮询） ----
_login_lock = threading.Lock()
_login_state: Dict[str, Any] = {
    "state": "idle",  # idle | running | success | error
    "message": "",
    "started_at": None,
    "finished_at": None,
}


def _set_login(state: str, message: str) -> None:
    with _login_lock:
        _login_state["state"] = state
        _login_state["message"] = message
        if state in ("success", "error"):
            _login_state["finished_at"] = datetime.now(timezone.utc).isoformat()


def auth_login(config_path: Optional[str] = None) -> dict:
    with _login_lock:
        if _login_state["state"] == "running":
            raise RuntimeError("已有登录流程在运行")
        _login_state.update(
            state="running",
            message="正在打开浏览器…",
            started_at=datetime.now(timezone.utc).isoformat(),
            finished_at=None,
        )

    def _do_login():
        try:
            detected = detect_system_browser()
            channel = None if detected is None else detected
            login = BrowserLogin(
                channel=channel,
                on_message=lambda m: _set_login("running", m),
            )
            with _login_lock:
                _login_state["message"] = "请在浏览器中登录你的 Xbox / Microsoft 账号…"
            bundle = login.capture()

            mgr = _get_mgr(config_path)
            cfg = mgr.load()
            cfg.token = bundle.access_token
            cfg.token_issued_at = datetime.now(timezone.utc).isoformat()
            cfg.token_expires_in = bundle.expires_in
            if bundle.refresh_token:
                cfg.refresh_token = bundle.refresh_token
            mgr.save(cfg)
            _set_login("success", "登录成功，Token 已保存。")
        except Exception as exc:  # noqa: BLE001 登录失败兜底
            _set_login("error", f"登录失败：{exc}")

    threading.Thread(target=_do_login, daemon=True).start()
    return {"ok": True, "state": "running", "message": "正在打开浏览器…"}


def auth_login_status() -> dict:
    with _login_lock:
        return dict(_login_state)


# ---------------------------------------------------------------------------
# 同步
# ---------------------------------------------------------------------------


def sync_start(
    games: Optional[List[str]] = None,
    force: bool = False,
    max_photos: Optional[int] = None,
    page_size: Optional[int] = None,
    config_path: Optional[str] = None,
) -> dict:
    mgr = _get_mgr(config_path)
    cfg = mgr.load()
    if not cfg.token:
        raise RuntimeError("未配置 Token，请先登录或填写 Token")

    if games:
        games = [g.strip().upper() for g in games if g.strip().upper() in SUPPORTED_GAMES]
    else:
        games = list(cfg.enabled_games)
    if not games:
        raise RuntimeError("没有指定有效的游戏")

    try:
        runner.start(
            config_path=mgr.path,
            games=games,
            force=force,
            max_photos=max_photos,
            page_size=page_size,
        )
    except RuntimeError as exc:
        raise RuntimeError(str(exc)) from exc
    return {"ok": True, "message": "同步已启动", "games": games}


def sync_progress() -> dict:
    return runner.progress.snapshot()


def sync_stop() -> dict:
    stopped = runner.stop()
    return {
        "ok": True,
        "stopped": stopped,
        "message": "已发送取消请求" if stopped else "当前没有运行中的任务",
    }


# ---------------------------------------------------------------------------
# 照片浏览
# ---------------------------------------------------------------------------


def list_photos(
    game: Optional[str] = None,
    month: Optional[str] = None,
    q: Optional[str] = None,
    limit: int = 48,
    offset: int = 0,
    config_path: Optional[str] = None,
) -> dict:
    mgr = _get_mgr(config_path)
    cfg = mgr.load()
    db_path = cfg.effective_database_path(mgr.path.parent)
    if not db_path.exists():
        return {"total": 0, "items": []}

    with PhotoDatabase(db_path).connect() as db:
        rows = db.all_photos(game)
        if month:
            rows = [r for r in rows if (r["submission_time_utc"] or "").startswith(month)]
        if q:
            ql = q.lower()
            rows = [
                r
                for r in rows
                if ql in (r["title"] or "").lower()
                or ql in r["photo_id"]
                or ql in (r["game"] or "").lower()
            ]
        total = len(rows)
        page = rows[offset : offset + limit]
        items = []
        for r in page:
            items.append(
                {
                    "photo_id": r["photo_id"],
                    "game": r["game"],
                    "title": r["title"] or "",
                    "description": r["description"] or "",
                    "submission_time_utc": r["submission_time_utc"] or "",
                    "month": (r["submission_time_utc"] or "")[:7],
                    "local_path": r["local_path"] or "",
                    "downloaded_at": r["downloaded_at"] or "",
                }
            )
    return {"total": total, "items": items}


def photo_meta(photo_id: str, config_path: Optional[str] = None) -> dict:
    mgr = _get_mgr(config_path)
    cfg = mgr.load()
    db_path = cfg.effective_database_path(mgr.path.parent)
    if not db_path.exists():
        raise LookupError("照片不存在")
    with PhotoDatabase(db_path).connect() as db:
        row = db.get_photo(photo_id)
    if row is None:
        raise LookupError("照片不存在")
    return {
        "photo_id": row["photo_id"],
        "game": row["game"],
        "title": row["title"] or "",
        "description": row["description"] or "",
        "submission_time_utc": row["submission_time_utc"] or "",
        "url": row["url"] or "",
        "local_path": row["local_path"] or "",
        "downloaded_at": row["downloaded_at"] or "",
    }


def photo_image(photo_id: str, config_path: Optional[str] = None) -> bytes:
    """返回本地图片文件的字节内容；不存在时抛出 LookupError。"""
    mgr = _get_mgr(config_path)
    cfg = mgr.load()
    db_path = cfg.effective_database_path(mgr.path.parent)
    if not db_path.exists():
        raise LookupError("照片不存在")
    with PhotoDatabase(db_path).connect() as db:
        row = db.get_photo(photo_id)
    if row is None or not row["local_path"]:
        raise LookupError("照片文件不存在")
    path = Path(row["local_path"])
    if not path.exists():
        raise LookupError(f"本地文件不存在: {path}")
    return path.read_bytes()


# ---------------------------------------------------------------------------
# PyO3 桥接入口（供 Tauri 嵌入式桌面端调用）
# ---------------------------------------------------------------------------


def call_service(name: str, args_json: str) -> str:
    """按名字调用服务函数，返回 JSON 字符串。

    参数通过 JSON 字符串传递（dict 按关键字展开）；返回值若非字节则 JSON 编码。
    """
    args = _json.loads(args_json) if args_json else {}
    func = globals().get(name)
    if func is None:
        raise ValueError(f"未知服务函数: {name}")
    result = func(**args) if isinstance(args, dict) else func(*args)
    if isinstance(result, bytes):
        return _json.dumps(
            {"__bytes__": base64.b64encode(result).decode("ascii")},
            ensure_ascii=False,
        )
    return _json.dumps(result, ensure_ascii=False, default=str)


def call_bytes(name: str, args_json: str) -> bytes:
    """按名字调用返回原始字节的服务函数（如图片内容）。"""
    args = _json.loads(args_json) if args_json else {}
    func = globals().get(name)
    if func is None:
        raise ValueError(f"未知服务函数: {name}")
    return func(**args) if isinstance(args, dict) else func(*args)
