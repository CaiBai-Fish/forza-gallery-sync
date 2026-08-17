# Forza Gallery Sync

Forza Horizon 照片自动同步工具：通过 Forza Gallery API 获取用户游戏内照片，自动下载**原图**到本地备份。

Forza Horizon 系列游戏内拍摄的照片不会保存在本地，而是上传到 Forza Gallery。本工具通过官方 API 拉取照片列表并批量下载 `photoCdnPath` 原图，配合 SQLite 增量记录，只下载新照片。

## 功能特性

- ✅ 支持 **FH5 / FH6** 两个游戏图库
- ✅ **增量同步**：以照片 URL 中提取的唯一 ID（photo UUID）判断是否已下载，不依赖文件名
- ✅ **Token 自动刷新**：access token 过期时自动用 refresh_token 刷新（OAuth2 refresh_token 流程），并持久化轮换后的新 token
- ✅ **标准 OAuth2 一键登录**：`forza-sync login` 按标准授权码 + PKCE 流程（参考微软身份平台文档），浏览器登录任意 Xbox/Microsoft 账号即自动获取 token（支持两步验证）
- ✅ **分页支持**：自动探测 API 分页参数（`page/pageSize`、`skip/take`、`offset/limit`、`pageNumber/pageSize`），支持超一页数据
- ✅ 按 `游戏/年/月` 组织目录，文件名含时间、标题、照片 ID
- ✅ 每张图旁保存 `.json` 元数据（游戏、标题、描述、上传时间、原图 URL）
- ✅ 多线程并发下载、失败重试、单张失败不影响整体
- ✅ 命令行：`config`（初始化）/ `sync`（同步）/ `status`（状态）
- ✅ 配置与代码分离（配置文件默认在用户配置目录）

## 安装

要求 Python 3.9+。

```bash
cd ForzaGallerySync
pip install -r requirements.txt
# 安装为命令行工具
pip install -e .

# 可选：浏览器自动登录（需额外安装 Playwright 与 Chromium）
pip install -r requirements-login.txt
playwright install chromium
# 国内网络可设置镜像下载浏览器：
# $env:PLAYWRIGHT_DOWNLOAD_HOST='https://npmmirror.com/mirrors/playwright/'
```

## 快速开始

### 1. 获取 Token（任选其一）

**方式 A：浏览器一键登录（推荐，任意 Xbox 账号）**

```bash
# 默认：自动检测系统浏览器（Edge → Chrome → Firefox），无需额外下载浏览器
forza-sync login

# 指定浏览器
forza-sync login --browser msedge    # Microsoft Edge
forza-sync login --browser chrome    # Google Chrome
forza-sync login --browser firefox   # Mozilla Firefox
forza-sync login --browser chromium  # Playwright 自带 Chromium（未检测到系统浏览器时兜底）
```

会弹出浏览器窗口，按**标准 OAuth 2.0 授权码 + PKCE 流程**完成登录：
1. 打开 `api.forza.net/connect/authorize` 授权端点（携带 PKCE `code_challenge` 与 `state`）
2. 未登录时跳转微软登录页（`login.live.com`），登录你的 Xbox / Microsoft 账号（支持两步验证）
3. 授权完成后工具捕获回调中的 `code`（不加载 `forza.net` 回调页，防止授权码被消费）
4. 用 `code + code_verifier` 在令牌端点换取 `access_token` 与 `refresh_token` 并保存

登录态持久化，之后 Token 过期会自动刷新。

**方式 B：手动配置**

```bash
forza-sync config
```

按提示输入：
- **Bearer Token**（必填，来自 Forza 网页/应用登录后获取）
- **刷新 Token**（推荐，用于自动刷新，防止 Token 过期）
- 下载目录（回车使用默认 `~/ForzaPhotos`）
- 启用游戏（回车默认 FH5、FH6）

> Token 属于敏感信息，输入时不回显。也可用 `forza-sync config set <键> <值>` 直接写入，例如：
> `forza-sync config set token <值>`、`forza-sync config set refresh_token <值>`

### 2. 执行同步

```bash
# 同步所有启用游戏
forza-sync sync

# 只同步 FH5
forza-sync sync --game FH5

# 强制重新下载（覆盖本地已存在文件）
forza-sync sync --force

# 调试：只处理前 10 张
forza-sync sync --max 10
```

### 3. 查看状态与 Token

```bash
# 查看同步状态（已同步数量、最近同步时间等）
forza-sync status

# 查看 Token 状态（是否过期、剩余有效期）
forza-sync token

# 强制刷新 Token
forza-sync token refresh
```

## 目录结构

```
ForzaPhotos/
├── FH5/
│   └── 2024/
│       └── 02/
│           ├── 20240216_112427_Forza_442a6e68.jpg
│           └── 20240216_112427_Forza_442a6e68.json   # 元数据
└── FH6/
    └── 2026/
        └── 08/
```

文件名格式：`{YYYYMMDD_HHMMSS}_{标题}_{photoId}.jpg`（标题为空时省略标题段）。

## 配置项

配置文件默认位置：
- Windows：`%APPDATA%\forza-sync\config.json`
- Linux/macOS：`~/.config/forza-sync/config.json`

可用环境变量 `FORZA_SYNC_CONFIG` 覆盖路径。参考 `config.example.json`：

| 配置项 | 说明 | 默认 |
| --- | --- | --- |
| `token` | Forza Bearer Token（access token） | 空 |
| `refresh_token` | 刷新 Token，用于自动续期 access token | 空 |
| `token_issued_at` | 最近一次获取/刷新 access token 的时间（自动维护） | 空 |
| `token_expires_in` | access token 有效期秒数（自动维护） | 0 |
| `download_dir` | 照片保存目录 | `~/ForzaPhotos` |
| `database_path` | SQLite 数据库路径 | `<配置目录>/forza_sync.db` |
| `page_size` | 每页数量 | 50 |
| `pagination` | 分页方案（`auto` 自动探测） | `auto` |
| `timeout` | 请求超时（秒） | 30 |
| `retries` | 失败重试次数 | 3 |
| `workers` | 并发下载线程数 | 4 |
| `verify_ssl` | 是否校验 SSL | `true` |
| `enabled_games` | 启用的游戏列表 | `["FH5","FH6"]` |

可用 `forza-sync config set <键> <值>` 修改单项，例如：

```bash
forza-sync config set download_dir D:/Backup/ForzaPhotos
forza-sync config set page_size 100
```

## API 说明

```
GET https://api.forza.net/api/v4/me/gallery/{FH5|FH6}
Authorization: Bearer <token>
```

返回结构：

```json
{
  "results": [
    {
      "title": "照片标题",
      "description": null,
      "submissionTimeUtc": "2024-02-16T11:24:27Z",
      "photoCdnPath": "https://...原图URL...",
      "thumbnailCdnPath": "https://...缩略图URL...",
      "previewCdnPath": "https://...预览图URL..."
    }
  ],
  "pagingInfo": { "totalRecords": 42 }
}
```

- `photoCdnPath` 可直接用于下载原图，无需拼接
- 真实 URL 结构：`.../galleryv2images/{图库ID}/{photo UUID}/{版本}`，
  其中 **photo UUID 是 URL 中最后一个 UUID**，工具用它作为照片唯一 ID
  （前面那个 UUID 是游戏图库 ID，所有照片相同，不能用作照片 ID）
- 分页参数未公开：工具首次同步会**自动探测**可用方案并沿用；若 API 变化，可将 `pagination` 手动设为 `page` / `skip` / `offset` / `page_number` 之一

### 标准 OAuth 授权码登录（login）

```
GET https://api.forza.net/connect/authorize
    ?client_id=nuxt-spa
    &redirect_uri=https://forza.net/callback   # 该 client 白名单内的回调地址
    &response_type=code
    &scope=openid profile offline_access
    &state=<随机>
    &code_challenge=<PKCE S256>
    &code_challenge_method=S256
```

外部身份提供方为 Microsoft（`login.live.com`）。登录完成回调携带 `code`，随后：

```
POST https://api.forza.net/connect/token
grant_type=authorization_code
code=<code>
redirect_uri=https://forza.net/callback
client_id=nuxt-spa
code_verifier=<PKCE verifier>
```

> 注意：OpenIddict（ID2074）不允许在授权码交换请求中携带 `scope` 参数（scope 已在授权阶段绑定）。

### Token 刷新

```
POST https://api.forza.net/connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=refresh_token
refresh_token=<refresh_token>
scope=openid+profile+offline_access
client_id=nuxt-spa
```

响应包含 `access_token`（Bearer，`expires_in` 约 55 分钟）、`id_token` 以及**轮换后的新 `refresh_token`**。
工具会在：
- 同步前检查 access token 是否临近过期（提前 60 秒）
- 请求返回 401 时自动刷新并重试一次
两个时机自动刷新，并把新的 token 对持久化到配置。

> 由于 refresh_token 是轮换制（每次刷新都换新），请勿在多个地方同时使用同一个 refresh_token，否则会互相挤掉。

## 错误处理

| 场景 | 处理方式 |
| --- | --- |
| Token 过期 / 无效（401/403） | 有 refresh_token 时自动刷新并重试；否则报错并提示重新配置 |
| refresh_token 失效 / 过期 | 报错并提示重新登录获取新 refresh_token |
| API 请求失败 | 按指数退避自动重试 |
| 网络异常 / 超时 | 按指数退避自动重试 |
| 图片下载失败 / 空内容 | 重试后记录失败项，继续处理其余照片 |
| JSON 格式变化 | 单条坏数据跳过；整体结构异常则报错 |
| 重复文件 / 重复照片 | 以 photo ID 判重，自动跳过 |

## 项目结构

```
forza_sync/
├── __init__.py      # 包信息
├── __main__.py      # python -m forza_sync 入口
├── cli.py           # 命令行（config/login/sync/token/status）
├── config.py        # 配置加载 / 保存 / 校验
├── auth.py          # Token 管理 + OAuth2 refresh_token 自动刷新
├── oauth.py         # 标准 OAuth2 授权码 + PKCE 流程（授权URL / 授权码交换）
├── login.py         # 浏览器驱动登录（系统 Edge/Chrome/Firefox 自动检测）
├── api_client.py    # Forza Gallery API 客户端 + 分页探测 + 401 自动重试
├── database.py      # SQLite 增量记录
├── naming.py        # 文件名生成与净化
├── downloader.py    # 图片下载与元数据
├── sync.py          # 同步编排
└── errors.py        # 异常定义
```

## 测试

```bash
pip install pytest
pytest
```

## 后续扩展（预留）

- **Windows GUI**：图形界面
- **NAS 自动同步**：同步后增量推送
- **定时任务**：周期性自动同步（配合 `login` + 自动刷新可长期无人值守）
- **更多 Forza 版本**：扩展 `SUPPORTED_GAMES`

## 免责声明

本工具仅用于个人照片备份，请遵守 Forza 相关服务条款，勿滥用 API。
