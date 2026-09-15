# Forza Gallery Sync

Forza Horizon 照片自动同步工具：通过 Forza Gallery API 获取用户游戏内照片，自动下载**原图**到本地备份。

Forza Horizon 系列游戏内拍摄的照片不会保存在本地，而是上传到 Forza Gallery。本工具通过官方 API 拉取照片列表并批量下载 `photoCdnPath` 原图，配合 SQLite 增量记录，只下载新照片。

## 功能特性

- ✅ 支持 **FH6 / FM / FH5 / FH4 / FM7** 五个游戏图库（显示名：Forza Horizon 6 / Forza Motorsport / Forza Horizon 5 / Forza Horizon 4 / Forza Motorsport 7）
- ✅ **增量同步**：以照片 URL 中提取的唯一 ID（photo UUID）判断是否已下载，不依赖文件名
- ✅ **Token 自动刷新**：access token 过期时自动用 refresh_token 刷新（OAuth2 refresh_token 流程），并持久化轮换后的新 token
- ✅ **标准 OAuth2 一键登录**：`forza-sync login`（或桌面版「设置」页一键登录）按标准授权码 + PKCE 流程（参考微软身份平台文档），浏览器登录任意 Xbox/Microsoft 账号即自动获取 token（支持两步验证）
- ✅ **分页支持**：自动探测 API 分页参数（`page/pageSize`、`skip/take`、`offset/limit`、`pageNumber/pageSize`），支持超一页数据
- ✅ 按 `游戏/年/月` 组织目录，文件名含时间、标题、照片 ID
- ✅ 照片详细信息（游戏、标题、描述、上传时间、原图 URL）直接存入 SQLite 数据库
- ✅ 多线程并发下载、失败重试、单张失败不影响整体
- ✅ 命令行：`config`（配置）/ `login`（浏览器一键登录）/ `sync`（同步）/ `token`（Token 管理）/ `status`（状态）
- ✅ 配置与代码分离（配置文件默认在用户配置目录）
- ✅ 桌面版**内置 Python 运行时**：发布为自包含目录，解压即用，**首次运行自动准备运行时**（干净目录→程序目录，否则→默认安装目录），脱离 Python 环境即可运行

## 安装

要求 Python 3.9+。

```bash
cd ForzaGallerySync
pip install -r requirements.txt
# 安装为命令行工具（含浏览器登录所需的 playwright）
pip install -e .

# 浏览器自动登录默认使用系统浏览器（Edge/Chrome/Firefox），无需下载额外浏览器。
# 仅当系统没有浏览器、需回退 Playwright Chromium 时才需要下载浏览器：
#   playwright install chromium
# 国内网络可设置镜像下载浏览器：
#   $env:PLAYWRIGHT_DOWNLOAD_HOST='https://npmmirror.com/mirrors/playwright/'
```

> **命令名说明**：命令行工具通过 `pip install -e .` 安装为 **`forza-sync`** 命令。
> 桌面版程序名为 **`forza-gallery-sync.exe`**，是独立 GUI 窗口（**不含命令行参数模式**）；
> 脚本 / 定时任务请使用 `forza-sync` 命令行。下文 CLI 示例均以 **`forza-sync`** 为准。

## CLI应用

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

## 管理控制台（桌面应用）

内置一个**桌面原生窗口**管理界面（WinUI 3 + Python.NET 内嵌 Python），
在独立桌面窗口中完成配置、登录、同步与照片浏览，无需手敲命令。

> 架构说明：Python 解释器通过 **Python.NET** 直接嵌入桌面应用进程，前端
> **WinUI 3（C# + XAML）** 调用 :mod:`forza_sync.service` 的纯函数。Python 运行时（含 `forza_sync` 包）
> **内嵌在程序内**，首次运行时自动解压（干净目录→程序目录，否则→默认安装目录），
> 用户无需单独安装 Python / Node / Rust / WebView2，也无需运行安装程序。

界面包含四个模块（导航按「图库 / 任务」分组，左侧为浏览与统计，右侧为任务与配置）：

| 窗口 | 功能 |
| --- | --- |
| 📊 总览 | 照片总量、按游戏分布、最近同步记录、最新照片预览；只读统计 + 快捷入口 |
| 🖼️ 照片库 | 自适应缩略图网格 + 详情大图（Hero 转场）、按游戏/月份筛选、搜索、分页 |
| 🔄 同步 | 本次任务参数（选游戏 / 数量上限 / 强制重下）、实时进度、已用时长与剩余时间、失败明细 |
| ⚙️ 设置 | 浏览器一键登录、Token 刷新、下载目录、网络与并发、启用游戏、检查更新 |

> 职责划分：**「同步」页只放本次任务的参数**，**每页数量 / 并发 / 超时 / 重试 / 启用游戏等全局配置统一在「设置」页**，
> 避免同一份配置在多处重复维护。左侧导航页脚常驻显示账号状态，标题栏在同步进行中显示进度。

### 开发调试（WinUI 3）

要求：.NET 8+ SDK、conda 环境 `FGS`（Python 3.13）。

```bash
cd web
dotnet build -p:Platform=x64          # 编译
dotnet run -p:Platform=x64            # 运行桌面窗口
```

> Python 运行时定位：程序目录 / 默认安装目录的 `python\` → 环境变量 `FORZA_SYNC_PYTHON_HOME` →
> 内嵌资源 zip（`make-runtime.ps1` 生成，首次运行自动解压）→ 均失败时给出明确错误。
> 开发时可设置 `FORZA_SYNC_PYTHON_HOME` 指向本地 Python 环境（**优先级高于安装目录里的运行时**，
> 避免机器上装过打包版后一直跑到旧版本）；
> 想让运行时/数据库落在指定目录，可设置 `FORZA_SYNC_INSTALL_DIR` / `FORZA_SYNC_APP_DIR`。

#### UI 结构与开发约定

- 视觉规范全部走主题资源：语义色定义在 `web/Styles/Controls.xaml` 的 `ThemeDictionaries`
  （浅色 / 深色两套），页面统一用 `CardBorderStyle` / `CaptionTextStyle` 等共享样式，
  不写死颜色，跟随系统浅色 / 深色主题自动适配。
- 新增页面时引用共享样式即可，无需重复定义卡片外观。
- **本项目 XAML 编译器的两个已知坑**（踩到时会报难以定位的
  `MSB3073: XamlCompiler.exe 已退出，代码为 1`，且没有任何具体错误信息）：
  1. 不要给 `GridView.ItemWidth` / `ItemHeight` 赋值（内部 `ItemsWrapGrid` 面板）——
     会让代码生成阶段失败。缩略图尺寸改为在代码里直接设置 `ItemsWrapGrid.ItemWidth/ItemHeight`
     （见 `GalleryPage.OnGridSizeChanged`）。
  2. 不要对 `InfoBar.IsOpen` 使用 `x:Bind`——同样会让代码生成失败。
     改用经典 `{Binding}` 并给页面设置 `DataContext`（见 `SyncPage`），
     或改用内联提示条（见 `SettingsPage`）。
- 多数情况下 MSB3073 的真正原因是 **C# 编译错误**（XAML 编译器在代码生成前需要加载程序集），
  排查时先确认 `dotnet build` 输出里是否存在 `error CS`。
- **检查更新以 `CHANGELOG.md` 为准**，三级来源任一成功即返回：
  1. 远程 CHANGELOG，**依次尝试 3 个镜像**（GitHub raw → jsDelivr → jsDelivr fastly），
     规避单一域名在部分网络下不可用；走静态文件下载，不消耗 GitHub API 配额
  2. 本地 `CHANGELOG.md`（从程序目录、可执行文件目录逐级向上、cwd、仓库根依次查找）
  3. GitHub Releases API 兜底（有 60 次/小时的匿名限额）

  设置页会把该版本的更新说明一并展示。发布新版本时记得在 CHANGELOG 里补上 `## [x.y.z]` 章节，
  否则检查更新读不到。（`CHANGELOG.md` **不随程序打包**，打包版依赖远程下载。）
- **Markdown 渲染用开源控件**：更新说明（CHANGELOG 章节）交给
  `CommunityToolkit.WinUI.UI.Controls.Markdown`（MIT，底层是 Markdig）渲染，
  不要自己手写 Markdown 解析。它会带入 `ColorCode`（代码块高亮）等依赖，属正常。
- **照片相关操作统一走 `web/Services/PhotoActions.cs`**（总览页与照片库共用）：
  「复制图片」把图片本身写入剪贴板并附带文件路径；「用默认应用打开」走
  `Process.Start(UseShellExecute: true)` 交给系统按文件类型打开（**不要**用 explorer，那是选中文件）；
  「打开所在文件夹」才是 explorer `/select`。新增这类操作请加到这个文件，不要在各页面重写一份。
- **解析 Python 返回的 JSON 必须用 `Models.Json.Deserialize<T>`**：它带 `SnakeCaseLower` 命名策略，
  能把 Python 的 `snake_case` 字段映射到 C# 的 `PascalCase` 属性。若用裸的
  `System.Text.Json.JsonSerializer.Deserialize<T>`，字段会静默映射失败、拿到默认值（如 `false`），
  表现为"后端返回正确但界面状态不对"。
- **共享元素（Hero）转场**统一走 `web/Services/HeroTransition.cs`（总览页的最新照片预览与
  照片库详情共用）。实现要点：用覆盖全页的 `Image` 承载动画，缩放/平移走 `RenderTransform`
  不触发布局；起止矩形每帧从实际元素测量，因此窗口缩放/滚动后依然准确；用会话号
  （`_previewSession` / `_detailSession`）让仍在飞行中的异步流程作废，避免快速连点串台。
  新增此类转场时请沿用同一套约定：**先用缓存缩略图铺好布局并同时起帧，原图与元数据在后台加载完再替换**。

### 打包桌面版程序（无需 Python / Node / Rust 环境）

桌面版把应用、.NET / Windows App SDK 运行时与 Python 运行时（含 `forza_sync`
包与 `requests` 依赖）一起发布为**自包含目录**，不再需要安装程序：**首次运行时
程序自己准备 Python 运行时**，**完全脱离本机 Python 环境**即可运行。

```bash
cd web

# 一键构建（产物：web/dist/ForzaGallerySync-0.5.0-win-x64/，另附同名 .zip）
powershell -ExecutionPolicy Bypass -File .\make-gui.ps1

# 只要目录、不压缩
powershell -ExecutionPolicy Bypass -File .\make-gui.ps1 -NoZip
```

流程（`make-gui.ps1` 内部）：
1. 生成内嵌 Python 运行时 `python-runtime.zip`（`forza_sync` 源码/依赖更新时自动重新生成）
2. 发布桌面应用为**自包含目录**（exe + .NET 运行时 + Windows App SDK，运行时 zip 内嵌在 exe 内）
3. 写入发布清单 `app-files.txt`（程序据此判断是否运行在**干净目录**）
4. 压缩整个目录为 `ForzaGallerySync-<版本>-win-x64.zip`

首次运行时，程序按「程序目录是否干净」决定运行时落点：

| 程序目录状态 | Python 运行时位置 | 数据库位置 |
| --- | --- | --- |
| **干净目录**：只有发布文件（含 `app-files.txt`）与程序自己生成的文件 | 程序目录 `python\`（便携模式，整个目录可整体搬移） | 程序目录 |
| **其它情况**：目录内还有别的文件 | `%LOCALAPPDATA%\Programs\ForzaGallerySync\python\`（默认安装目录） | 默认安装目录 |

> - 默认安装目录可用环境变量 `FORZA_SYNC_INSTALL_DIR` 覆盖；程序目录不可写时自动回退到安装目录。
> - 解压出的运行时会在 `python\.runtime-id` 记录内嵌 zip 的 SHA256：程序升级
>   （内嵌 zip 变化）后自动重新解压，避免旧运行时残留。

> 说明：
> - 项目已配置 `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`，
>   无需系统预装 Windows App Runtime；发布目录同时自带 .NET 运行时。
> - 内嵌运行时包含 `forza_sync`、`requests` 依赖链与 **playwright**（含 `node.exe` 驱动、`greenlet`、`pyee`）：
>   打包版的「浏览器一键登录」开箱可用，用户无需安装任何 Python 包（仅当无系统浏览器、需回退
>   Playwright Chromium 时才需 `playwright install chromium`）。
> - WinUI 3 无法以单文件发布（XAML 依赖同目录原生库），因此桌面版发布为**目录 + zip**。
> - 运行时定位优先级：**程序目录/安装目录 `python\`** → `FORZA_SYNC_PYTHON_HOME` →
>   内嵌资源 zip（首次运行解压到程序目录或默认安装目录）。
> - 打包脚本（`make-runtime.ps1` / `make-gui.ps1` / `make-cli.ps1`）不硬编码
>   本机路径：默认从 PATH 自动探测 `python`，也可用 `-PythonEnv <目录>` /
>   `-Python <python.exe>` 显式指定。
> - 开发调试用 `dotnet build -p:Platform=x64` / `dotnet run -p:Platform=x64`；
>   也可用 `make-runtime.ps1` 生成内嵌运行时。
> - GUI 负责配置、登录、同步与照片浏览。

### 打包纯后端 CLI（Nuitka，独立单文件）

纯后端 CLI 是纯 Python 程序（`forza_sync` 包），可用 **Nuitka** 编译为
**自包含的单文件 exe**，脱离 Python 环境直接运行（无需 conda / pip）：

```bash
# 编译为 cli-dist/forza-sync.exe（约 34MB，zstd 压缩）
powershell -ExecutionPolicy Bypass -File .\web\make-cli.ps1
```

> 说明：
> - 入口为仓库根目录 `cli_entry.py`（调用 `forza_sync.cli.main`），
>   Nuitka 把解释器 + `forza_sync` + `requests` + `certifi` + `sqlite3` 全部编译进单个 exe。
> - 需要本机安装 MSVC（Nuitka 自动定位 VS 的 `vcvarsall`）与 `pip install nuitka`。
> - CLI 与 GUI 可**分别发布**：服务器 / 脚本 / 定时任务用 `forza-sync.exe`（轻量），
>   桌面用户用 `forza-gallery-sync.exe`（GUI）。两者共用同一份配置与数据库。
> - CLI 单文件为保持轻量**未包含 playwright**（驱动约 100MB），因此 `forza-sync login`
>   需要本机可导入 playwright（`pip install playwright`）；也可先用 **GUI 版的「浏览器一键登录」**
>   完成登录，CLI 会复用同一份配置与 Token。

## 目录结构

```
ForzaPhotos/
├── FH5/
│   └── 2024/
│       └── 02/
│           └── 20240216_112427_Forza_442a6e68.jpg
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
| `database_path` | SQLite 数据库路径 | 桌面版：`<程序目录或默认安装目录>/forza_sync.db`；CLI/开发：`<配置目录>/forza_sync.db` |
| `page_size` | 每页数量 | 50 |
| `pagination` | 分页方案（`auto` 自动探测） | `auto` |
| `timeout` | 请求超时（秒） | 30 |
| `retries` | 失败重试次数 | 3 |
| `workers` | 并发下载线程数 | 4 |
| `verify_ssl` | 是否校验 SSL | `true` |
| `enabled_games` | 启用的游戏列表 | `["FH5","FH6"]`（可含 FH6/FM/FH4/FM7） |

可用 `forza-sync config set <键> <值>` 修改单项，例如：

```bash
forza-sync config set download_dir D:/Backup/ForzaPhotos
forza-sync config set page_size 100
```

## API 说明

```
GET https://api.forza.net/api/v4/me/gallery/{FH6|FM|FH5|FH4|FM7}
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
├── forza_sync/               # Python 核心（CLI 与桌面服务共用）
│   ├── __init__.py           # 包信息
│   ├── __main__.py           # python -m forza_sync 入口
│   ├── cli.py                # 命令行（config/login/sync/token/status）
│   ├── config.py             # 配置加载 / 保存 / 校验
│   ├── auth.py               # Token 管理 + OAuth2 refresh_token 自动刷新
│   ├── oauth.py              # 标准 OAuth2 授权码 + PKCE 流程（授权URL / 授权码交换）
│   ├── login.py              # 浏览器驱动登录（系统 Edge/Chrome/Firefox 自动检测）
│   ├── api_client.py         # Forza Gallery API 客户端 + 分页探测 + 401 自动重试
│   ├── database.py           # SQLite 增量记录
│   ├── naming.py             # 文件名生成与净化
│   ├── downloader.py         # 图片下载与元数据
│   ├── sync.py               # 同步编排
│   ├── runner.py             # 后台同步运行器（桌面端 / 服务复用）
│   ├── service.py            # 纯函数服务层（供 Python.NET 桌面端调用，无 HTTP）
│   └── errors.py             # 异常定义
├── tests/                    # pytest 单元测试
│   ├── __init__.py
│   ├── test_api_client.py
│   ├── test_auth.py
│   ├── test_database.py
│   ├── test_login.py
│   ├── test_naming.py
│   └── test_sync.py
├── web/                      # 桌面应用（WinUI 3 + C# + Python.NET 内嵌 Python）
│   ├── App.xaml / App.xaml.cs       # 应用入口（含未处理异常日志、主窗口引用）
│   ├── MainWindow.xaml / .cs        # 主窗口（分组 NavigationView + 标题栏状态区 + 页脚账号状态）
│   ├── Styles/Controls.xaml         # 主题资源：语义色 ThemeDictionaries + 卡片/文本/按钮样式
│   ├── Services/                    # Python.NET 桥接（PythonHost / PyBridge / Logger）+ HeroTransition 共享元素动画
│   ├── Assets/                      # 应用图标（ico + 标题栏用 png）
│   ├── ForzaGallerySync.csproj      # 项目配置（嵌入 python-runtime.zip）
│   ├── Views/                # 四个页面（总览 / 照片库 / 同步 / 设置）
│   ├── ViewModels/           # MVVM 视图模型（含 Hero 转场 / 侧边栏动画 / 进度计时）
│   ├── Models/               # 数据模型（snake_case ↔ PascalCase 映射）
│   ├── Services/             # Python.NET 桥接（PythonHost / PyBridge / Logger）
│   ├── Converters/           # XAML 值转换器
│   ├── make-runtime.ps1      # 生成 Python 运行时（python-runtime.zip，内嵌进应用 exe）
│   ├── make-gui.ps1          # 打包桌面版程序（web/dist/ForzaGallerySync-*-win-x64/ + .zip）
│   ├── make-cli.ps1          # Nuitka 打包纯后端 CLI（cli-dist/forza-sync.exe）
│   └── publish-single.ps1    # （已废弃）别名，转交 make-gui.ps1
├── CHANGELOG.md              # 更新日志（GitHub Release 说明由 workflow 自动生成）
├── cli_entry.py              # Nuitka 打包 CLI 的入口（调用 forza_sync.cli.main）
├── config.example.json       # 配置示例
├── pyproject.toml            # 打包与 `forza-sync` 命令入口
├── requirements.txt          # 核心依赖（requests 依赖链 + playwright 浏览器登录）
└── requirements-dev.txt      # 测试依赖（pytest）
```

## 测试

```bash
pip install -r requirements-dev.txt
pytest
```

## 版本记录

> 完整更新日志见 [CHANGELOG.md](CHANGELOG.md)；GitHub Release 的发布说明
> 由 `.github/workflows/build-release.yml` 自动从 CHANGELOG 对应版本章节生成。

### v0.5.0
- 变更：**取消 MSI 打包与 Setup 安装程序**，构建直接输出 GUI 版（`make-gui.ps1` →
  `web/dist/ForzaGallerySync-0.5.0-win-x64/` + 同名 `.zip`）与 CLI 版（`cli-dist/forza-sync.exe`）
- 新增：GUI **首次运行自动准备 Python 运行时** —— 程序目录为**干净目录**时解压到程序目录（便携），
  否则解压到默认安装目录 `%LOCALAPPDATA%\Programs\ForzaGallerySync`（可用 `FORZA_SYNC_INSTALL_DIR` 覆盖）；
  运行时按内嵌 zip 的 SHA256 标记校验，程序升级后自动重新解压
- 移除：`web/make-msi.ps1`、`web/msi-generate.ps1`、`installer/`（Setup + MSI 工程）、WiX 工具依赖

### v0.4.2
- 新增：**MSI 安装包**（WiX v4，per-user、x64，`make-msi.ps1` → `web/dist/ForzaGallerySync-0.4.2.msi`）；支持静默安装/卸载，卸载自动保留数据库

### v0.4.1
- 修复：CLI（Nuitka）在标准 CPython 下构建 sqlite3.dll 冲突；GUI 安装程序 / CLI / 应用 exe 增加应用图标

### v0.4.0
- 新增：**检查更新**功能（设置页「关于与更新」卡片 + GitHub Releases 源；可选 GitHub token 提升限流）
- 打包：桌面版内置 Python 运行时（`make-runtime.ps1` 生成内嵌资源包），**脱离 Python 环境运行**
- 发布：改为**安装程序模式**（`make-installer.ps1` → `web/dist/ForzaGallerySync-Setup-0.4.0.exe`，
  约 191MB）：安装时把 Python/.NET 运行时**解压到安装目录**，运行时直接使用安装目录环境；
  内置安装/卸载（开始菜单快捷方式 + 卸载注册表项）
- 发布：纯后端 CLI 用 **Nuitka 编译为独立单文件**（`make-cli.ps1` → `cli-dist/forza-sync.exe`，约 34MB），
  与 GUI 版可**分别发布**；已含 `sqlite3.dll`，脱离 Python 环境验证通过
- 移除：旧 Tauri 前端 `web-legacy/`（如需可从 git 历史找回）
- 优化：照片 Hero 转场动画改用 RenderTransform（不触发布局，更流畅）；下拉框「全部」选项；同步时 token 过期自动刷新
- 日志：统一日志模块（`%TEMP%\ForzaGallerySync\logs\`，按天分文件）

### v0.3.0
- 重构：桌面管理控制台前端由 Vue 3 + Tauri 替换为 **WinUI 3（C# + XAML）**
- 架构：Python.NET 内嵌 Python（替代 PyO3），复用 `forza_sync` 全部后端逻辑，仍无 HTTP 服务、无端口
- 页面：仪表盘 / 照片库 / 同步 / 设置 四个模块完整复刻
- 说明：旧 Tauri 前端保留在 `web-legacy/` 目录供参考

### v0.2.1
- 修复：桌面版改用 Windows GUI 子系统，双击启动不再闪现命令行窗口（零控制台）
- 修复：CLI 中文输出按控制台代码页自动编码（GBK/UTF-8 自适应），兼容中文系统 PowerShell
- 说明：CLI 在交互式终端的输出顺序（提供 `cmd /c start "" /wait` / `Start-Process -Wait` 同步方式）

### v0.1.0
- 首个版本：Forza 照片同步 CLI + 桌面管理控制台（Tauri + PyO3 内嵌 Python，无 HTTP 服务）

## 免责声明

本工具仅用于个人照片备份，请遵守 Forza 相关服务条款，勿滥用 API。
