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
- ✅ 桌面版**内置 Python 运行时**，发布为**安装程序**：安装时把 Python/.NET 运行时解压到安装目录，脱离 Python 环境即可运行

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
> **WinUI 3（C# + XAML）** 调用 :mod:`forza_sync.service` 的纯函数。安装程序把 Python 运行时（含 `forza_sync` 包）
> 随应用一起**解压到安装目录**，程序运行时直接使用安装目录里的环境，
> 用户无需单独安装 Python / Node / Rust / WebView2。

界面包含四个模块：

| 窗口 | 功能 |
| --- | --- |
| 📊 仪表盘 | 照片统计、按游戏/月份分布、Token 状态、最近同步记录、快速操作 |
| 🖼️ 照片库 | 浏览已同步照片（网格 + 详情）、按游戏/月份筛选、搜索、分页 |
| 🔄 同步 | 选择游戏、配置参数、启动/停止同步、实时进度与失败明细 |
| ⚙️ 设置 | 浏览器一键登录、Token 刷新、下载目录 / 并发 / 分页等配置、检查更新 |

### 开发调试（WinUI 3）

要求：.NET 8+ SDK、conda 环境 `FGS`（Python 3.13）。

```bash
cd web
dotnet build -p:Platform=x64          # 编译
dotnet run -p:Platform=x64            # 运行桌面窗口
```

> Python 运行时定位：安装目录 `python\` → 环境变量 `FORZA_SYNC_PYTHON_HOME` →
> 内嵌资源 zip（`make-runtime.ps1` 生成）→ 均未找到时给出明确错误。
> 开发时可设置 `FORZA_SYNC_PYTHON_HOME` 指向本地 Python 环境。

### 打包桌面版安装程序（无需 Python / Node / Rust 环境）

桌面版把应用、.NET / Windows App SDK 运行时与 Python 运行时（含 `forza_sync`
包与 `requests` 依赖）一起打成**安装程序**。安装时把全部运行时解压到
**安装目录**，程序运行时直接使用安装目录里的环境，**完全脱离本机 Python
环境**即可运行。

```bash
cd web

# 一键构建安装程序（自动完成以下四步，产物：web/dist/ForzaGallerySync-Setup-0.4.1.exe，约 191MB）
powershell -ExecutionPolicy Bypass -File .\make-installer.ps1
```

流程（`make-installer.ps1` 内部）：
1. 发布桌面应用为**自包含目录**（exe + .NET 运行时 + Windows App SDK）
2. 用 `make-runtime.ps1` 把 Python 运行时**解压**到 `app\python`
3. 把整个 `app\` 压缩为 `payload.zip` 内嵌进安装程序
4. 发布单文件安装程序（`installer\` 项目，自包含，不依赖目标机器 .NET）

安装程序用法：

```bash
ForzaGallerySync-Setup-0.4.1.exe                     # 交互式安装
ForzaGallerySync-Setup-0.4.1.exe --install [目录]     # 静默安装
ForzaGallerySync-Setup-0.4.1.exe --uninstall          # 卸载
```

安装位置默认 `%LOCALAPPDATA%\Programs\ForzaGallerySync`，开始菜单含应用与卸载快捷方式，并写入卸载注册表项。
照片数据库（`forza_sync.db`）默认存放在**安装目录**内；卸载时会自动把数据库保留到用户配置目录
（`%APPDATA%\forza-sync\`），不会因卸载而丢失。

> 说明：
> - 项目已配置 `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`，
>   无需系统预装 Windows App Runtime；安装包同时自带 .NET 运行时。
> - 运行时定位优先级：**安装目录 `python\`** → `FORZA_SYNC_PYTHON_HOME` →
>   内嵌资源 zip（开发/便携回退，解压到 `%LOCALAPPDATA%\ForzaGallerySync\runtime\v1`）。
> - 打包脚本（`make-runtime.ps1` / `make-installer.ps1` / `make-cli.ps1`）不硬编码
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
| `database_path` | SQLite 数据库路径 | 桌面安装版：`<安装目录>/forza_sync.db`；CLI/开发：`<配置目录>/forza_sync.db` |
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
│   ├── App.xaml / App.xaml.cs       # 应用入口（含未处理异常日志）
│   ├── MainWindow.xaml / .cs        # 主窗口（NavigationView 导航）
│   ├── ForzaGallerySync.csproj      # 项目配置（嵌入 python-runtime.zip）
│   ├── Views/                # 四个页面（仪表盘 / 照片库 / 同步 / 设置）
│   ├── ViewModels/           # MVVM 视图模型（含 Hero 转场 / 侧边栏动画）
│   ├── Models/               # 数据模型（snake_case ↔ PascalCase 映射）
│   ├── Services/             # Python.NET 桥接（PythonHost / PyBridge / Logger）
│   ├── Converters/           # XAML 值转换器
│   ├── make-runtime.ps1      # 生成 Python 运行时（python-runtime.zip，可解压目录）
│   ├── make-installer.ps1    # 打包桌面版安装程序（web/dist/ForzaGallerySync-Setup-*.exe）
│   ├── make-cli.ps1          # Nuitka 打包纯后端 CLI（cli-dist/forza-sync.exe）
│   └── publish-single.ps1    # （已废弃）旧单文件发布，转交 make-installer.ps1
├── installer/                # 安装程序工程（自包含单文件，内嵌 payload.zip）
│   ├── ForzaGallerySync.Setup.csproj
│   └── Program.cs            # 安装/卸载/快捷方式/卸载注册表逻辑
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
