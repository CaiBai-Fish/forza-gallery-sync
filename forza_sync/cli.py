"""命令行入口。

命令：
  forza-sync config              交互式初始化配置（Token / 刷新Token / 下载目录 / 游戏）
  forza-sync config show         查看当前配置
  forza-sync config set <键> <值> 设置单项配置
  forza-sync login               浏览器登录任意 Xbox 账号并自动捕获 Token
  forza-sync sync [--game X] [--force] [--max N] [--page-size N]   执行同步
  forza-sync token [status|refresh]   查看 / 强制刷新 Token
  forza-sync status              查看同步状态
"""

from __future__ import annotations

import argparse
import getpass
import logging
import sys
from datetime import datetime, timezone
from typing import List, Optional

from . import __version__
from .auth import TokenManager
from .config import (
    SUPPORTED_GAMES,
    Config,
    ConfigManager,
)
from .database import PhotoDatabase
from .errors import AuthError, ConfigError, ForzaSyncError
from .login import BrowserLogin, detect_system_browser
from .sync import SyncService

log = logging.getLogger("forza_sync")


# ---------------------------------------------------------------------------
# 工具函数
# ---------------------------------------------------------------------------
def _mask_token(token: str) -> str:
    """打码 Token，仅展示首尾各 4 位。"""
    if not token:
        return "(未设置)"
    if len(token) <= 8:
        return "*" * len(token)
    return f"{token[:4]}{'*' * (len(token) - 8)}{token[-4:]}"


def _setup_logging(verbose: bool, quiet: bool) -> None:
    if quiet:
        level = logging.WARNING
    elif verbose:
        level = logging.DEBUG
    else:
        level = logging.INFO
    logging.basicConfig(
        level=level,
        format="[%(asctime)s] [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )


def _get_config_manager(args: argparse.Namespace) -> ConfigManager:
    return ConfigManager(getattr(args, "config_path", None))


# ---------------------------------------------------------------------------
# config 命令
# ---------------------------------------------------------------------------
def cmd_config(args: argparse.Namespace) -> int:
    mgr = _get_config_manager(args)
    cfg = mgr.load()

    if args.action == "show":
        print(f"配置文件: {mgr.path}")
        print(f"Token: {_mask_token(cfg.token)}")
        print(f"刷新 Token: {_mask_token(cfg.refresh_token)}")
        print(f"下载目录: {cfg.effective_download_dir()}")
        print(f"数据库: {cfg.effective_database_path(mgr.path.parent)}")
        print(f"启用游戏: {', '.join(cfg.enabled_games)}")
        print(
            f"分页方案: {cfg.pagination}（page_size={cfg.page_size}, workers={cfg.workers}）"
        )
        return 0

    if args.action == "set":
        key, value = args.key, args.value
        return _config_set(mgr, cfg, key, value)

    # 交互式初始化
    print("=== Forza Gallery 照片同步工具配置 ===")
    print(f"配置文件位置: {mgr.path}")
    print("（直接回车表示保留当前值）\n")

    try:
        current = cfg.token
        token = getpass.getpass("Bearer Token（留空保留当前值）: ")
        if token.strip():
            cfg.token = token.strip()
        elif not current:
            print("警告：未设置 Token，同步功能将不可用。")
    except (EOFError, OSError):
        print("无法在终端读取输入，请改用: forza-sync config set token <值>")
        return 1

    try:
        rt = getpass.getpass("刷新 Token（用于自动刷新，可留空）: ")
        if rt.strip():
            cfg.refresh_token = rt.strip()
    except (EOFError, OSError):
        pass

    download_dir = input(f"下载目录（默认 {cfg.effective_download_dir()}）: ").strip()
    if download_dir:
        cfg.download_dir = download_dir

    games_raw = input(
        f"启用游戏（逗号分隔，可选: {', '.join(SUPPORTED_GAMES)}，默认 {'、'.join(cfg.enabled_games)}）: "
    ).strip()
    if games_raw:
        games = [g.strip().upper() for g in games_raw.split(",") if g.strip()]
        cfg.enabled_games = [g for g in games if g in SUPPORTED_GAMES]
        if not cfg.enabled_games:
            print(f"警告：没有合法的游戏名，合法值为 {', '.join(SUPPORTED_GAMES)}，保留原配置。")

    mgr.save(cfg)
    print("\n配置已保存 ✅")
    return 0


_CONFIG_KEYS = {
    "token": "str",
    "refresh_token": "str",
    "token_expires_in": "int",
    "download_dir": "str",
    "database_path": "str",
    "page_size": "int",
    "pagination": "str",
    "timeout": "int",
    "retries": "int",
    "workers": "int",
    "verify_ssl": "bool",
    "user_agent": "str",
}


def _config_set(mgr: ConfigManager, cfg: Config, key: str, value: str) -> int:
    key = key.strip()
    if key not in _CONFIG_KEYS:
        print(f"未知配置项: {key}，可用项: {', '.join(sorted(_CONFIG_KEYS))}")
        return 1
    if value is None:
        print(f"缺少配置项 {key} 的值，用法: forza-sync config set {key} <值>")
        return 1
    kind = _CONFIG_KEYS[key]

    try:
        if kind == "int":
            setattr(cfg, key, int(value))
        elif kind == "bool":
            setattr(cfg, key, value.strip().lower() in ("1", "true", "yes", "on"))
        else:
            setattr(cfg, key, value.strip())
    except ValueError:
        print(f"配置项 {key} 需要整数，收到: {value!r}")
        return 1

    try:
        cfg.validate()
    except ConfigError as exc:
        print(f"配置无效: {exc}")
        return 1

    mgr.save(cfg)
    masked_keys = ("token", "refresh_token")
    print(f"已设置 {key} = {_mask_token(value) if key in masked_keys else value}")
    return 0


# ---------------------------------------------------------------------------
# login 命令
# ---------------------------------------------------------------------------
def cmd_login(args: argparse.Namespace) -> int:
    mgr = _get_config_manager(args)
    browser = getattr(args, "browser", None) or "auto"
    if browser == "auto":
        detected = detect_system_browser()
        if detected:
            browser = detected
        else:
            print("未检测到系统浏览器，回退到 Playwright Chromium。")
            browser = "chromium"
    channel = None if browser == "chromium" else browser
    login = BrowserLogin(
        profile_dir=args.profile,
        headless=args.headless,
        timeout=args.timeout,
        channel=channel,
        on_message=lambda msg: print(msg),
    )
    browser_name = {
        "msedge": "Microsoft Edge",
        "chrome": "Google Chrome",
        "firefox": "Mozilla Firefox",
    }.get(channel, "Playwright Chromium")
    print("===== 浏览器登录（自动捕获 Token） =====")
    print(f"浏览器：{browser_name}；登录窗口打开后，请登录你的 Xbox / Microsoft 账号。")
    try:
        bundle = login.capture()
    except AuthError as exc:
        print(f"错误：{exc}")
        return 1

    cfg = mgr.load()
    cfg.token = bundle.access_token
    cfg.token_issued_at = datetime.now(timezone.utc).isoformat()
    cfg.token_expires_in = bundle.expires_in
    if bundle.refresh_token:
        cfg.refresh_token = bundle.refresh_token
    mgr.save(cfg)

    print("\n✅ 登录成功，Token 已保存到配置")
    print(f"   access_token : {_mask_token(cfg.token)}")
    if bundle.refresh_token:
        print(f"   refresh_token: {_mask_token(cfg.refresh_token)}")
        print(f"   有效期       : {bundle.expires_in} 秒（约 {bundle.expires_in // 60} 分钟）")
        print("之后运行 `forza-sync sync` 即可自动同步；Token 过期会自动刷新。")
    else:
        print("   refresh_token: （本次未捕获到，保留配置中原有值）")
        print("提示：本次未捕获到 refresh_token，自动续期可能不可用。")
        print("      可运行 `forza-sync token refresh` 验证；或手动 `config set refresh_token <值>`。")
    return 0


# ---------------------------------------------------------------------------
# sync 命令
# ---------------------------------------------------------------------------
def cmd_sync(args: argparse.Namespace) -> int:
    mgr = _get_config_manager(args)
    cfg = mgr.load()

    if not cfg.token:
        print("错误：未配置 Token，请先运行 `forza-sync config` 或 `forza-sync config set token <值>`")
        return 1

    # --page-size 临时覆盖配置
    if getattr(args, "page_size", None) is not None:
        cfg.page_size = max(1, args.page_size)

    # 确定要同步的游戏
    games: List[str] = []
    if args.game:
        for g in args.game.split(","):
            g = g.strip().upper()
            if g in SUPPORTED_GAMES:
                games.append(g)
            else:
                print(f"警告：忽略未知游戏 {g!r}，合法值: {', '.join(SUPPORTED_GAMES)}")
        if not games:
            print("错误：没有指定有效的游戏")
            return 1
    else:
        games = list(cfg.enabled_games)
        if not games:
            print("错误：配置中未启用任何游戏")
            return 1

    db_path = cfg.effective_database_path(mgr.path.parent)
    token_manager = TokenManager(
        mgr.path,
        timeout=cfg.timeout,
        retries=cfg.retries,
        verify_ssl=cfg.verify_ssl,
        user_agent=cfg.user_agent,
    )
    with PhotoDatabase(db_path).connect() as db:
        service = SyncService(cfg, db, token_manager=token_manager)
        grand = {"synced": 0, "skipped": 0, "failed": 0}
        for game in games:
            print(f"\n===== 同步 {game} =====")
            stats = service.sync_game(game, force=args.force, max_photos=args.max)
            grand["synced"] += stats.synced
            grand["skipped"] += stats.skipped
            grand["failed"] += stats.failed
            if stats.failed_items:
                print(f"[{game}] 失败 {len(stats.failed_items)} 张:")
                for url, reason in stats.failed_items[:10]:
                    print(f"  - {url}: {reason}")
                if len(stats.failed_items) > 10:
                    print(f"  ... 其余 {len(stats.failed_items) - 10} 条略")

        print("\n===== 同步完成 =====")
        print(f"新增下载: {grand['synced']}")
        print(f"已存在跳过: {grand['skipped']}")
        print(f"失败: {grand['failed']}")
        print(f"下载目录: {cfg.effective_download_dir()}")
        return 0 if grand["failed"] == 0 else 1


# ---------------------------------------------------------------------------
# status 命令
# ---------------------------------------------------------------------------
def cmd_status(args: argparse.Namespace) -> int:
    mgr = _get_config_manager(args)
    cfg = mgr.load()
    db_path = cfg.effective_database_path(mgr.path.parent)

    print("===== 配置 =====")
    print(f"配置文件: {mgr.path}")
    print(f"Token: {_mask_token(cfg.token)}")
    print(f"刷新 Token: {_mask_token(cfg.refresh_token)}")
    print(f"下载目录: {cfg.effective_download_dir()}")
    print(f"数据库: {db_path}")
    print(f"启用游戏: {', '.join(cfg.enabled_games)}")

    if not db_path.exists():
        print("\n（数据库尚不存在，还没有同步记录）")
        return 0

    with PhotoDatabase(db_path).connect() as db:
        print("\n===== 已同步照片 =====")
        by_game = db.count_by_game()
        if not by_game:
            print("（暂无已同步照片）")
        for row in by_game:
            print(f"  {row['game']}: {row['n']} 张")
        print(f"  合计: {db.count_photos()} 张")

        print("\n===== 最近同步时间 =====")
        states = db.get_sync_state()
        if not states:
            print("（暂无同步记录）")
        for row in states:
            last = row["last_sync_at"] or "未知"
            print(f"  {row['game']}: 最近同步 {last}，本次拉取 {row['total_records']} 张")
    return 0


# ---------------------------------------------------------------------------
# token 命令
# ---------------------------------------------------------------------------
def cmd_token(args: argparse.Namespace) -> int:
    mgr = _get_config_manager(args)
    cfg = mgr.load()
    token_manager = TokenManager(
        mgr.path,
        timeout=cfg.timeout,
        retries=cfg.retries,
        verify_ssl=cfg.verify_ssl,
        user_agent=cfg.user_agent,
    )

    if args.action == "refresh":
        if not cfg.refresh_token:
            print("错误：未配置刷新 Token。请运行 `forza-sync config` 或 `forza-sync config set refresh_token <值>`")
            return 1
        try:
            token_manager.refresh()
        except AuthError as exc:
            print(f"错误：{exc}")
            return 1
        print("Token 已刷新 ✅（新的 access_token 与 refresh_token 已保存到配置）")
        return 0

    # status
    info = token_manager.status()
    print("===== Token 状态 =====")
    print(f"Access Token: {info['masked_token']}")
    print(f"刷新 Token: {info['masked_refresh_token']}")
    print(f"已过期: {'是' if info['expired'] else '否'}")
    if info["expires_in"] is None:
        print("剩余有效期: 未知（配置中无签发时间，无法判断）")
    else:
        print(f"剩余有效期: {info['expires_in']} 秒（约 {info['expires_in'] // 60} 分钟）")
    return 0


# ---------------------------------------------------------------------------
# 主入口
# ---------------------------------------------------------------------------
def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="forza-sync",
        description="Forza Horizon 照片自动同步工具：通过 Forza Gallery API 下载并备份照片。",
    )
    parser.add_argument("--version", action="version", version=f"forza-sync {__version__}")
    parser.add_argument("--config", dest="config_path", default=None,
                        help="指定配置文件路径（默认使用用户配置目录）")
    parser.add_argument("-v", "--verbose", action="store_true", help="输出调试日志")
    parser.add_argument("-q", "--quiet", action="store_true", help="只输出警告与错误")

    sub = parser.add_subparsers(dest="command", metavar="命令")

    # config
    p_config = sub.add_parser("config", help="初始化 / 查看 / 修改配置")
    p_config.add_argument("action", nargs="?", choices=["show", "set"], default=None,
                          help="子操作：show 查看，set 修改；缺省为交互式初始化")
    p_config.add_argument("key", nargs="?", help="set 的配置项名")
    p_config.add_argument("value", nargs="?", help="set 的配置值")

    # sync
    p_sync = sub.add_parser("sync", help="执行照片同步")
    p_sync.add_argument("--game", default=None, help="指定游戏，逗号分隔，如 FH5,FH6；缺省同步所有启用游戏")
    p_sync.add_argument("--force", action="store_true", help="强制重新下载（即使数据库中已存在）")
    p_sync.add_argument("--max", type=int, default=None, help="最多处理前 N 张照片（调试用）")
    p_sync.add_argument("--page-size", type=int, default=None, help="覆盖每页数量设置")

    # status
    sub.add_parser("status", help="查看同步状态")

    # login
    p_login = sub.add_parser("login", help="浏览器登录任意 Xbox 账号并自动捕获 Token")
    p_login.add_argument(
        "--browser", choices=["auto", "msedge", "chrome", "firefox", "chromium"], default="auto",
        help="浏览器：auto 自动检测系统浏览器（Edge/Chrome/Firefox，默认）；"
             "msedge/chrome/firefox 指定系统浏览器；chromium 用 Playwright 自带浏览器",
    )
    p_login.add_argument("--headless", action="store_true", help="无头模式（调试用，默认有头）")
    p_login.add_argument("--timeout", type=int, default=600, help="等待登录超时秒数（默认 600）")
    p_login.add_argument("--profile", default=None, help="浏览器用户数据目录（持久化登录态）")

    # token
    p_token = sub.add_parser("token", help="查看 / 强制刷新 Token")
    p_token.add_argument(
        "action", nargs="?", choices=["status", "refresh"], default="status",
        help="status 查看状态（默认）；refresh 强制刷新",
    )

    return parser


def main(argv: Optional[List[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    _setup_logging(args.verbose, args.quiet)

    if args.command is None:
        parser.print_help()
        return 0

    # 允许 --page-size 临时覆盖
    if getattr(args, "page_size", None) is not None:
        args.page_size = max(1, args.page_size)

    try:
        if args.command == "config":
            return cmd_config(args)
        if args.command == "login":
            return cmd_login(args)
        if args.command == "sync":
            return cmd_sync(args)
        if args.command == "token":
            return cmd_token(args)
        if args.command == "status":
            return cmd_status(args)
        parser.print_help()
        return 0
    except ForzaSyncError as exc:
        log.error("%s", exc)
        return 1
    except KeyboardInterrupt:
        log.warning("已取消")
        return 130


if __name__ == "__main__":
    sys.exit(main())
