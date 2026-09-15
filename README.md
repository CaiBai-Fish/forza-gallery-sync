# Forza Gallery Sync

Forza 系列游戏内拍摄的照片不会保存在本地，而是上传到 Forza Gallery。本工具通过官方 API
拉取照片列表并批量下载 `photoCdnPath` 原图到本地，配合 SQLite 增量记录只下载新照片。

提供两种使用方式：**命令行 `forza-sync`** 与 **桌面应用 `forza-gallery-sync.exe`**（WinUI 3，
内置 Python 运行时，解压即用、无需安装 Python）。

## 功能特性

- **五个游戏图库**：FH6 / FM / FH5 / FH4 / FM7
- **增量同步**：以照片 URL 中的 photo UUID 判重，不依赖文件名，重复照片自动跳过
- **Token 自动刷新**：access token 临近过期或请求返回 401 时，用 refresh_token 自动续期并持久化轮换后的新 token
- **标准 OAuth2 一键登录**：按授权码 + PKCE 流程驱动系统浏览器登录任意 Xbox / Microsoft 账号（支持两步验证）
- **分页自动探测**：自动识别 `page/pageSize`、`skip/take`、`offset/limit`、`pageNumber/pageSize` 等方案，支持超过一页的数据
- **目录按 `游戏/年/月` 组织**，文件名含时间、标题与照片 ID
- 照片元数据（游戏、标题、描述、上传时间、原图 URL）随下载一并写入 SQLite
- 多线程并发下载、失败重试，单张失败不影响整体
- 配置与代码分离，配置文件位于用户配置目录

## 安装

### 桌面版（普通用户）

从 [Releases](https://github.com/CaiBai-Fish/forza-gallery-sync/releases) 下载并运行
**`ForzaGallerySync-<版本>-setup.exe`**，向导式安装，**不需要管理员权限**。

- 安装到 `%LOCALAPPDATA%\Programs\ForzaGallerySync`，自带桌面与开始菜单快捷方式、标准卸载入口；
- 目录内已包含全部运行时（.NET、Windows App SDK、Python），**无需预装任何东西**；
- 程序目录对当前用户可写，所以内置的"一键自动更新"在安装版上正常工作。

若更偏好免安装，同页面的 `ForzaGallerySync-<版本>-win-x64.zip` 解压即用，功能一致。

### 开发环境

要求 Python 3.9+（桌面版开发环境使用 3.13）。

```bash
pip install -r requirements.txt
pip install -e .          # 安装为 forza-sync 命令
```

> 浏览器一键登录默认使用系统浏览器（Edge / Chrome / Firefox），无需额外下载浏览器。
> 仅当系统没有浏览器、需回退 Playwright Chromium 时才需要：
> `playwright install chromium`（国内网络可设 `PLAYWRIGHT_DOWNLOAD_HOST` 指向镜像）。

**命令名说明**：CLI 通过 `pip install -e .` 安装为 **`forza-sync`**；桌面版程序名为
**`forza-gallery-sync.exe`**，是独立 GUI 窗口，**不接受命令行参数**。脚本与定时任务请用 `forza-sync`。

## CLI 使用

### 获取 Token

方式 A —— 浏览器一键登录（推荐）：

```bash
forza-sync login                    # 自动检测系统浏览器（Edge → Chrome → Firefox）
forza-sync login --browser msedge   # 指定系统浏览器：msedge / chrome / firefox
forza-sync login --browser chromium # Playwright 自带 Chromium（无系统浏览器时兜底）
```

按标准 OAuth 2.0 授权码 + PKCE 流程执行：打开授权端点 → 跳转 Microsoft 登录页 →
捕获回调中的授权码（不加载回调页，避免授权码被消费）→ 用 `code + code_verifier`
换取 `access_token` 与 `refresh_token` 并保存。登录态持久化，之后过期自动刷新。

方式 B —— 手动配置：

```bash
forza-sync config                                   # 交互式填写
forza-sync config set token <值>                    # 或直接写单项
forza-sync config set refresh_token <值>
```

交互式填写会依次询问：Bearer Token（必填）、刷新 Token（推荐）、下载目录（默认 `~/ForzaPhotos`）、
启用游戏（默认 FH5、FH6）。Token 为敏感信息，输入时不回显。

### 执行同步

```bash
forza-sync sync                  # 同步所有启用游戏
forza-sync sync --game FH5       # 只同步指定游戏，逗号分隔：--game FH6,FM
forza-sync sync --force          # 强制重新下载（覆盖已存在文件）
forza-sync sync --max 10         # 只处理前 N 张（调试用）
forza-sync sync --page-size 100  # 覆盖本次的每页数量
```

### 查看状态与 Token

```bash
forza-sync status            # 同步状态：已同步数量、最近同步时间
forza-sync token             # Token 状态：是否过期、剩余有效期
forza-sync token refresh     # 强制刷新 Token
forza-sync config show       # 查看当前配置
```

全局选项：`--config <路径>` 指定配置文件、`-v/--verbose` 输出调试日志、`-q/--quiet` 只输出警告与错误。

## 桌面应用

WinUI 3 原生窗口（C# + XAML）通过 **Python.NET** 在进程内嵌入 Python，直接调用
`forza_sync.service` 的纯函数——**无 HTTP 服务、无端口、无网络监听**。Python 运行时
（含 `forza_sync` 与依赖）内嵌在程序里，首次运行自动准备，用户无需安装 Python / Node / WebView2。

界面上没有需要手敲命令的操作，四个页面按「图库 / 任务」分组：

| 页面 | 功能 |
| --- | --- |
| 总览 | 照片总量、按游戏分布、最近同步记录、最新照片预览 |
| 照片库 | 自适应缩略图网格 + 详情大图、按游戏/月份筛选、搜索、分页；右键可复制图片、用默认应用打开、打开所在文件夹 |
| 同步 | 本次任务参数（选游戏 / 数量上限 / 强制重下）、实时进度、已用时长与预计剩余时间、失败明细 |
| 设置 | 浏览器一键登录、Token 刷新、下载目录、网络与并发、启用游戏、检查更新与自动更新 |

职责划分：**「同步」页只放本次任务的参数**，每页数量 / 并发 / 超时 / 重试 / 启用游戏等
全局配置统一在「设置」页。导航页脚常驻显示账号状态，标题栏在同步进行中显示进度。

### 自动更新

设置页发现新版本后可一键更新，流程是：**确认对话框** → 下载官方发布包 →
**SHA256 校验** → 退出应用 → 覆盖程序目录里的文件（只覆盖、不删除）→ 自动重启。

> 自动更新是"覆盖程序目录"，所以**程序目录必须对当前用户可写**。安装程序把程序装在
> `%LOCALAPPDATA%\Programs\ForzaGallerySync`（per-user、免管理员）正是为此；
> 手动把发布目录放到 `Program Files` 下会导致只能手动更新。

版本探测走**五路**（`api.github.com` 匿名限流 60 次/小时，实测经常 403，所以不能只看 API）：

| 顺序 | 来源 | 说明 |
| --- | --- | --- |
| 1 | Releases API | `releases/latest` 的 `tag_name` |
| 2 | Tags API | `tags` 的第一个 `name` |
| 3 | `releases/latest` 跳转 | 302 的 `Location` 指向 `/releases/tag/<tag>` |
| 4 | `tags` 页面 HTML | 页面里的 `/releases/tag/<tag>` 链接 |
| 5 | CHANGELOG 保底 | 第一个 `## [x.y.z]`；自身再有 raw → blob 页面 → 本地副本三级回退 |

哈希清单同样多源：`hashes` 分支的 `<版本>.txt`（每行 `<sha256>  <文件名>`）→
同分支的 `hashes.json` → Releases API 的 `digest`。前两者是静态文件，**不消耗 API 配额**。
**哈希不匹配或拿不到官方清单时都不会自动安装**，只给手动下载出口。

### 增量更新

更新默认走增量：只下载**相对上一版真正变化**的文件，而不是每次都重下约 130 MB 的完整包。

发布目录解压后约 269 MB，构成是：

| 部分 | 大小 | 跨版本变化 |
| --- | --- | --- |
| Python 运行时（约 3200 个文件） | 77 MB | 只在后端代码改动时变 |
| .NET / WinUI / Windows SDK 运行时 | 182 MB | 基本不变 |
| 应用自身代码 | 约 25 MB | 每次必变 |

工作方式：

1. CI 为每个"相对上一版变化"且 ≥256 KB 的文件生成一个单文件 zip 作为 Release 资产，
   并把逐文件清单 `increment.json`（`path` / `size` / `sha256`）发布到 `hashes` 分支。
2. 客户端取清单后**只比对本地实际文件的哈希**（不依赖"已安装版本"记录，不需要本地基线清单），
   只下载哈希不符或缺失的那些文件。
3. 变化的小文件（<256 KB）打成一个容器包——逐个小文件发资产会有几百个，
   而"一个小文件变了就整体回退完整包"又会让增量几乎永远不生效。
4. 每个文件逐个校验 SHA256，再重打包成与完整包同构的增量包交给替换脚本。

**任何一步不成立都会自动回退完整包**（没有清单、版本不符、哈希不符、下载失败、
本地缺失又无来源），完整包那条路径与之前完全一致。设置页可关闭"优先增量更新"。

### 单实例

同一登录会话内只允许一个窗口：重复启动时第二个实例**不建窗口**，用
`RegisterWindowMessage` 广播唤醒已有窗口（最小化则先还原），然后显式结束自己的进程
（`Environment.Exit(0)`，退出码 0）。`SetForegroundWindow` 被系统前台锁挡下时改用 Z 序置顶，
并在日志里区分"被前台锁挡住"与"句柄失效"。

### 安装程序

`web/make-installer.ps1` 用 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 把整个发布目录
打成单个 `ForzaGallerySync-<版本>-setup.exe`（向导式、简中/英文界面）：

```bash
powershell -ExecutionPolicy Bypass -File .\web\make-installer.ps1 `
  -SourceDir web\dist\ForzaGallerySync-1.0.4-win-x64 -Version 1.0.4
```

- **per-user 安装**（`PrivilegesRequired=lowest`）：装到 `%LOCALAPPDATA%\Programs\ForzaGallerySync`，
  不弹 UAC。这个位置可写，所以安装版同样支持内置自动更新。
- **固定目录名**（不带版本号）：升级就是覆盖同一个目录，不会堆出多个版本目录。
- **没有语言选择对话框**：`ShowLanguageDialog=auto` 按 Windows 界面语言自动匹配
  （中文系统直接中文、英文系统英文），匹配不到才问；重装时沿用上次选择。
- **安装程序自身单实例**（`SetupMutex`）：重复启动会提示已有安装在进行，不会两个安装程序
  互相覆盖文件与卸载注册表项。程序正在运行时安装/卸载也会先提示（`AppMutex`）。
- 快捷方式可选：开始菜单与桌面图标都是可勾选项（默认都建），取消勾选不会影响可卸载性
  （仍可从「设置 → 应用」或安装目录里的 `unins000.exe` 卸载）。
- **卸载时询问是否删除用户数据**（默认保留）：对话框会列出实际路径
  （`%APPDATA%\forza-sync\config.json` 与 `forza_sync.db`），并说明**照片文件在下载目录里、
  不受该选项影响**。勾选才删，不勾选就保留，方便重装后免于重新登录。
- 简体中文语言文件在 `web/Resources/Languages/ChineseSimplified.isl`（官方 Inno 不自带中文）。
  缺这个文件或应用图标时，脚本会自动退回英文 / 默认图标，不让构建失败。

验证脚本：`web/verify-installer.ps1` 会自动断言静默安装/卸载、卸载窗体控件齐全、
取消卸载时数据保留等 12 项；勾选删除数据那条分支不做自动化（原因见脚本头部注释），
需人工确认。

### 已知限制

- **发布目录是扁平结构**：自包含 WinUI 应用的根目录有 300 多个文件（DLL / winmd / `.pri`），
  看起来杂乱，但**这是平台约束，不能按用途分到 `runtime\`、`assets\` 等子目录**：
  运行时按固定相对路径查找它们，移动会导致启动失败（详见 `web/organize-release.ps1` 的说明）。
  能做的只有**减少文件数量**：WinUI 自带的多语言资源已在编译期裁剪到
  `zh-CN` / `zh-TW` / `en-us` 三种（原本 83 个语言目录），打包脚本只做校验不做删除。
- **便携版本不做自我替换**：自动更新是"用新版本文件覆盖程序目录"，不是安装包；
  程序目录不可写（例如装在 `Program Files`）时只提示手动更新。
- 覆盖**只针对新发布包里存在的文件**：程序目录里多余的文件会保留，不会被清理。
  因此从旧布局升级到新布局时，旧文件可能残留（不影响运行）。
- 覆盖发生在旧进程退出之后，替换脚本从系统临时目录运行；失败日志在
  `%TEMP%\ForzaGallerySync-update.log`。

### 开发调试

要求 .NET 8+ SDK，以及 conda 环境 `FGS`（Python 3.13）。

```bash
cd web
dotnet build -p:Platform=x64     # 编译
dotnet run -p:Platform=x64       # 运行
```

开发时用 `FORZA_SYNC_PYTHON_HOME` 指向本地 Python 环境（优先级高于安装目录里的运行时，
避免机器上装过打包版后一直跑到旧版本）。运行时与数据目录可用 `FORZA_SYNC_INSTALL_DIR` /
`FORZA_SYNC_APP_DIR` 指定。

### 打包

```bash
cd web

# GUI：自包含目录 + zip（版本号取自 pyproject.toml）
powershell -ExecutionPolicy Bypass -File .\make-gui.ps1
#   → web/dist/ForzaGallerySync-<版本>-win-x64/ 与同名 .zip（-NoZip 只出目录）

# 纯后端 CLI：Nuitka 编译为单文件（需本机 MSVC 与 pip install nuitka）
powershell -ExecutionPolicy Bypass -File .\make-cli.ps1
#   → cli-dist/forza-sync.exe（不含 playwright，体积更小）
```

GUI 发布目录自带 .NET / Windows App SDK 运行时（`WindowsPackageType=None` +
`WindowsAppSDKSelfContained=true`），无需系统预装 Windows App Runtime。
首次运行时按「程序目录是否为干净目录」决定 Python 运行时落点：

| 程序目录状态 | Python 运行时 / 数据库位置 |
| --- | --- |
| 干净目录（只含发布清单所列文件与程序自建文件） | 程序目录 `python\`（便携，整个目录可整体搬移） |
| 其它情况 | `%LOCALAPPDATA%\Programs\ForzaGallerySync`（可用 `FORZA_SYNC_INSTALL_DIR` 覆盖） |

运行时定位优先级：程序目录 / 安装目录 `python\` → `FORZA_SYNC_PYTHON_HOME` → 内嵌资源 zip。
解压出的运行时按内嵌 zip 的 SHA256 标记校验，程序升级后自动重新解压，避免旧运行时残留。

## 配置项

配置文件默认位置：Windows `%APPDATA%\forza-sync\config.json`；其它平台 `~/.config/forza-sync/config.json`。
可用环境变量 `FORZA_SYNC_CONFIG` 覆盖。完整示例见 `config.example.json`。

| 配置项 | 说明 | 默认 |
| --- | --- | --- |
| `token` | Forza Bearer Token（access token） | 空 |
| `refresh_token` | 刷新 Token，用于自动续期 | 空 |
| `token_issued_at` | 最近一次获取 / 刷新 access token 的时间（自动维护） | 空 |
| `token_expires_in` | access token 有效期秒数（自动维护） | 0 |
| `download_dir` | 照片保存目录 | `~/ForzaPhotos` |
| `database_path` | SQLite 数据库路径 | 桌面版：程序目录或安装目录；CLI / 开发：配置目录 |
| `page_size` | 每页数量 | 50 |
| `pagination` | 分页方案：`auto` / `page` / `skip` / `offset` / `page_number` / `none` | `auto` |
| `timeout` | 请求超时（秒） | 30 |
| `retries` | 失败重试次数 | 3 |
| `workers` | 并发下载线程数 | 4 |
| `verify_ssl` | 是否校验 SSL 证书 | `true` |
| `user_agent` | 请求 UA | `forza-sync/<版本>` |
| `enabled_games` | 启用的游戏列表 | `["FH5","FH6"]`，可选 FH6 / FM / FH5 / FH4 / FM7 |

## 目录结构与命名

```
ForzaPhotos/
├── FH5/
│   └── 2024/
│       └── 02/
│           └── 20240216_112427_符华_442a6e68.jpg
└── FH6/
    └── 2026/
        └── 08/
```

文件名格式：`{YYYYMMDD_HHMMSS}_{标题}_{photoId}.jpg`，标题为空时省略标题段。
时间取照片的上传时间（UTC 转本地）。

## API 说明

```
GET https://api.forza.net/api/v4/me/gallery/{FH6|FM|FH5|FH4|FM7}
Authorization: Bearer <token>
```

返回 `results` 数组与 `pagingInfo.totalRecords`；每条记录含 `title`、`description`、
`submissionTimeUtc`、`photoCdnPath`（原图，可直接下载）、`thumbnailCdnPath`、`previewCdnPath`。

- 照片唯一 ID 取自 `photoCdnPath` 中**最后一个 UUID**（前面的 UUID 是游戏图库 ID，所有照片相同）
- 分页参数未公开：首次同步会自动探测可用方案并沿用；API 变化时可手动指定 `pagination`
- 授权码 + PKCE 与 refresh_token 的具体请求参数见 `forza_sync/oauth.py` 与 `forza_sync/auth.py`

## 错误处理

| 场景 | 处理方式 |
| --- | --- |
| Token 过期 / 无效（401/403） | 有 refresh_token 时自动刷新并重试一次；否则报错并提示重新配置 |
| refresh_token 失效 | 报错并提示重新登录 |
| API 请求失败 / 网络超时 | 按指数退避自动重试 |
| 图片下载失败或内容为空 | 重试后记为失败项，继续处理其余照片 |
| 单条 JSON 数据异常 | 跳过该条；整体结构异常才报错 |
| 重复照片 | 以 photo ID 判重，自动跳过 |

> refresh_token 为轮换制（每次刷新都换新），请勿在多个地方同时使用同一个，否则会互相挤掉。

## 项目结构

```
├── forza_sync/                   # Python 核心（CLI 与桌面服务共用）
│   ├── cli.py                    # 命令行入口（config / login / sync / token / status）
│   ├── config.py                 # 配置加载 / 保存 / 校验、游戏列表与分页方案定义
│   ├── auth.py                   # Token 管理 + refresh_token 自动刷新
│   ├── oauth.py                  # OAuth2 授权码 + PKCE（授权 URL / 授权码交换）
│   ├── login.py                  # 浏览器登录（系统 Edge / Chrome / Firefox 自动检测）
│   ├── api_client.py             # Gallery API 客户端 + 分页探测 + 401 自动重试
│   ├── database.py               # SQLite 增量记录（photos / sync_state）
│   ├── naming.py                 # 文件名生成与净化
│   ├── downloader.py             # 图片下载与元数据
│   ├── sync.py                   # 同步编排
│   ├── runner.py                 # 后台同步运行器（桌面端复用）
│   ├── service.py                # 纯函数服务层（供 Python.NET 调用，无 HTTP）
│   ├── updates.py                # 检查更新（五路版本探测 + 哈希清单定位）
│   └── errors.py                 # 异常定义
├── tests/                        # pytest 单元测试
├── web/                          # 桌面应用（WinUI 3 + C# + Python.NET）
│   ├── App.xaml(.cs)             # 应用入口（含单实例检查）
│   ├── MainWindow.xaml(.cs)      # 主窗口：分组 NavigationView + 标题栏状态 + 页脚账号状态
│   ├── Views/                    # 四个页面：总览 / 照片库 / 同步 / 设置
│   ├── ViewModels/               # MVVM 视图模型（含 Hero 转场、进度计时）
│   ├── Models/                   # 数据模型（snake_case ↔ PascalCase）
│   ├── Services/                 # Python.NET 桥接（PythonHost / PyBridge / Logger）
│   │   ├── HeroTransition.cs     # 共享元素（缩略图 ↔ 大图）转场
│   │   ├── PhotoActions.cs       # 复制图片 / 用默认应用打开 / 定位文件
│   │   ├── SingleInstance.cs     # 单实例：互斥体 + 广播唤醒已有窗口
│   │   ├── AppVersion.cs         # 当前版本（从程序集信息读取）
│   │   └── UpdateService.cs      # 自动更新：下载、哈希校验、替换重启
│   ├── Converters/               # XAML 值转换器
│   ├── Styles/Controls.xaml      # 主题资源：语义色 + 卡片 / 文本 / 按钮样式
│   ├── Resources/                # 自动更新替换脚本 + 安装程序脚本（Inno Setup）
│   │   ├── apply-update.ps1      # 自动更新时的替换脚本（内嵌为资源）
│   │   ├── installer.iss         # EXE 安装程序定义
│   │   └── Languages/            # 安装程序界面语言（官方 Inno 不含简体中文）
│   ├── Assets/                   # 应用图标（发布目录里必须有，见 csproj 的 Assets 补发逻辑）
│   ├── make-runtime.ps1          # 生成内嵌 Python 运行时（python-runtime.zip）
│   ├── make-gui.ps1              # 打包桌面版（自包含目录 + zip）
│   ├── make-installer.ps1        # 生成 EXE 安装程序
│   ├── organize-release.ps1      # 发布目录校验（语言裁剪是否生效、关键文件在位）
│   └── make-cli.ps1              # Nuitka 打包 CLI
├── .github/workflows/            # CI：构建与发布（含产物哈希发布）
├── CHANGELOG.md                  # 变更历史（Release 说明由 CI 从此文件生成）
├── cli_entry.py                  # Nuitka 打包 CLI 的入口
├── config.example.json           # 配置示例
├── pyproject.toml                # 打包与 forza-sync 命令入口
└── requirements*.txt             # 运行 / 测试依赖
```

## 测试

```bash
# 本项目测试环境为 conda 环境 FGS（Python 3.13）
conda activate FGS
pip install -r requirements-dev.txt
pytest
```

## 变更历史

见 [CHANGELOG.md](CHANGELOG.md)。GitHub Release 的发布说明由
`.github/workflows/build-release.yml` 从 CHANGELOG 对应版本章节自动生成，
发布新版本时只需维护 CHANGELOG。

## 免责声明

本工具仅用于个人照片备份，请遵守 Forza 相关服务条款，勿滥用 API。
